// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Plugin.Script;

public sealed record ScriptEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";

    /// <summary>
    /// Shell to run the command with. When empty, bash is used.
    /// Supported values: "bash", "sh", and "pwsh".
    /// </summary>
    public string Shell { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Host-independent persistence for <see cref="ScriptEntry"/> collections.
/// Owns the JSON serialization options and the on-disk config path.
/// </summary>
internal sealed class ScriptStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dataDir;
    private readonly string _configPath;

    public ScriptStore(string dataDir)
    {
        _dataDir = dataDir;
        _configPath = Path.Join(dataDir, "scripts.json");
    }

    public List<ScriptEntry> Load()
    {
        if (!File.Exists(_configPath))
            return [];

        try
        {
            var json = File.ReadAllText(_configPath);
            var scripts = JsonSerializer.Deserialize<List<ScriptEntry>>(json, s_jsonOptions);
            return scripts ?? [];
        }
        catch (Exception ex)
        {
            // Surface I/O and JSON errors instead of masking them as "no
            // scripts" — a silent empty list would let the next Save()
            // overwrite a config file that is merely corrupt or locked.
            throw new InvalidOperationException(
                $"Failed to read script configuration from {_configPath}: {ex.Message}",
                ex
            );
        }
    }

    public void Save(IEnumerable<ScriptEntry> entries)
    {
        Directory.CreateDirectory(_dataDir);
        var json = JsonSerializer.Serialize(entries.ToList(), s_jsonOptions);

        // Atomically replace the target so a crash mid-write can't truncate _configPath.
        var tempPath = Path.Join(_dataDir, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_configPath))
                File.Replace(tempPath, _configPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _configPath);
        }
        catch
        {
            if (!File.Exists(tempPath))
            {
                throw;
            }

            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}

public sealed class ScriptService
{
    /// <summary>How long one script may run, during dictation and in a test.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // A test shows its output, so it is bounded; dictation keeps the unbounded original.
    internal const int TestOutputLimitBytes = 1024 * 1024;

    private readonly IPluginHostServices _host;
    private readonly ScriptStore _store;
    private bool _loadSucceeded;

    public ObservableCollection<ScriptEntry> Scripts { get; } = [];

    public ScriptService(IPluginHostServices host)
    {
        _host = host;
        _store = new ScriptStore(host.PluginDataDirectory);
        Load();
    }

    public void AddScript(ScriptEntry script)
    {
        Scripts.Add(script);
        Save();
    }

    public void RemoveScript(Guid id)
    {
        var script = Scripts.FirstOrDefault(s => s.Id == id);
        if (script is null)
        {
            return;
        }

        Scripts.Remove(script);
        Save();
    }

    public void UpdateScript(ScriptEntry updated)
    {
        for (var i = 0; i < Scripts.Count; i++)
        {
            if (Scripts[i].Id != updated.Id)
            {
                continue;
            }

            Scripts[i] = updated;
            Save();
            return;
        }
    }

    public void MoveUp(Guid id)
    {
        var index = IndexOf(id);
        if (index <= 0)
        {
            return;
        }

        Scripts.Move(index, index - 1);
        Save();
    }

    public void MoveDown(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0 || index >= Scripts.Count - 1)
        {
            return;
        }

        Scripts.Move(index, index + 1);
        Save();
    }

    public async Task<string> RunScriptsAsync(
        string text,
        PostProcessingContext context,
        CancellationToken ct
    )
    {
        var current = text;

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- explicit loop kept; the LINQ form switches enumerators and obscures the side effects.
        foreach (var script in Scripts.ToList())
        {
            if (!script.IsEnabled)
                continue;

            try
            {
                current = await RunSingleAsync(script, current, context, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _host.Log(PluginLogLevel.Warning, $"Script '{script.Name}' failed: {ex.Message}");
                // Return the text as-is up to this point; don't break the pipeline
            }
        }

        return current;
    }

    internal static (string FileName, IReadOnlyList<string> Arguments) ResolveShell(
        ScriptEntry script
    )
    {
        var shell = string.IsNullOrWhiteSpace(script.Shell)
            ? "bash"
            : script.Shell.Trim().ToLowerInvariant();

        return shell switch
        {
            "pwsh" or "powershell" => (
                "pwsh",
                ["-NoProfile", "-Command", script.Command]
            ),
            "sh" => ("sh", ["-c", script.Command]),
            // bash + any legacy/unknown value (e.g. an old "cmd" entry).
            _ => ("bash", ["-c", script.Command]),
        };
    }

    private Task<ProcessRunOutcome> StartAsync(
        ScriptEntry script,
        string text,
        PostProcessingContext context,
        int? maximumOutputBytes,
        CancellationToken ct
    )
    {
        var (fileName, arguments) = ResolveShell(script);

        var environment = new Dictionary<string, string>
        {
            ["TYPEWHISPER_APP_NAME"] = context.ActiveAppName ?? "",
            ["TYPEWHISPER_LANGUAGE"] = context.SourceLanguage ?? "",
            ["TYPEWHISPER_PROFILE"] = context.ProfileName ?? "",
        };

        return _host.Processes.RunOneShotAsync(
            new ProcessCommand(fileName, arguments, environment),
            new ProcessOneShotOptions(Timeout: Timeout, StandardInput: new Utf8ProcessInput(text))
            {
                MaximumStandardOutputBytes = maximumOutputBytes,
                MaximumStandardErrorBytes = maximumOutputBytes,
            },
            ct
        );
    }

    /// <summary>
    ///     Runs a draft once with the dictation timeout, empty TYPEWHISPER_* variables and bounded
    ///     output, without touching the saved scripts.
    /// </summary>
    internal async Task<ProcessRunOutcome> RunTestAsync(
        ScriptEntry script,
        string input,
        CancellationToken ct
    )
    {
        try
        {
            return await StartAsync(script, input, new PostProcessingContext(), TestOutputLimitBytes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _host.Log(PluginLogLevel.Info, $"Script test '{script.Name}' cancelled by caller.");
            throw;
        }
    }

    private async Task<string> RunSingleAsync(
        ScriptEntry script,
        string text,
        PostProcessingContext context,
        CancellationToken ct
    )
    {
        ProcessRunOutcome result;
        try
        {
            result = await StartAsync(script, text, context, maximumOutputBytes: null, ct);
        }
        catch (OperationCanceledException)
        {
            // The supervisor terminates and reaps the child.
            _host.Log(PluginLogLevel.Info, $"Script '{script.Name}' cancelled by caller.");
            throw;
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement -- part of a guard chain; a switch would only cover this branch.
        if (result.Status == ProcessRunStatus.TimedOut)
        {
            _host.Log(
                PluginLogLevel.Warning,
                $"Script '{script.Name}' timed out after {Timeout.TotalSeconds:0} seconds"
            );
            return text;
        }

        if (result.Status == ProcessRunStatus.StartFailed)
        {
            throw new InvalidOperationException(
                result.StartError ?? $"Could not start {ResolveShell(script).FileName}."
            );
        }

        var stdout = result.StandardOutputText;
        var stderr = result.StandardErrorText;
        if (result.ExitCode != 0)
        {
            _host.Log(
                PluginLogLevel.Warning,
                $"Script '{script.Name}' exited with code {result.ExitCode}: {stderr}"
            );
            return text;
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            _host.Log(PluginLogLevel.Info, $"Script '{script.Name}' stderr: {stderr}");
        }

        return stdout;
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < Scripts.Count; i++)
            if (Scripts[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>
    /// Replaces the entire script collection with <paramref name="entries"/> and persists.
    /// </summary>
    public void ReplaceAll(IEnumerable<ScriptEntry> entries)
    {
        // Save() throws on persistence failure. Snapshot the existing collection
        // first so we can restore it on failure; otherwise rejected edits would
        // stay active in memory while the UI reports the save as failed.
        var snapshot = Scripts.ToList();
        Scripts.Clear();
        foreach (var entry in entries)
            Scripts.Add(entry);

        try
        {
            Save();
        }
        catch
        {
            Scripts.Clear();
            foreach (var entry in snapshot)
                Scripts.Add(entry);
            throw;
        }
    }

    private void Load()
    {
        List<ScriptEntry> loaded;
        try
        {
            loaded = _store.Load();
        }
        catch (Exception ex)
        {
            _host.Log(PluginLogLevel.Warning, $"Failed to load script configuration: {ex.Message}");
            return;
        }

        foreach (var script in loaded)
            Scripts.Add(NormalizeEntry(script));
        _loadSucceeded = true;
    }

    /// <summary>
    /// Maps legacy shell aliases (e.g. "powershell") to their canonical form
    /// ("pwsh"). Applied on load so older saved entries round-trip without the
    /// SetItemsAsync validator rejecting them.
    /// </summary>
    internal static string NormalizeShellValue(string? shell)
    {
        var trimmed = (shell ?? "").Trim().ToLowerInvariant();
        return trimmed == "powershell" ? "pwsh" : trimmed;
    }

    private static ScriptEntry NormalizeEntry(ScriptEntry entry) =>
        entry.Shell == NormalizeShellValue(entry.Shell) ? entry : entry with { Shell = NormalizeShellValue(entry.Shell) };

    public void Save()
    {
        if (!_loadSucceeded)
        {
            // Refuse to write: the on-disk file failed to load and may still
            // contain valid scripts that an empty/in-memory state would clobber.
            _host.Log(
                PluginLogLevel.Warning,
                "Refusing to save script configuration because the existing file could not be loaded."
            );
            throw new InvalidOperationException(
                "Cannot save script configuration because the existing file could not be loaded."
            );
        }

        try
        {
            _store.Save(Scripts);
        }
        catch (Exception ex)
        {
            _host.Log(PluginLogLevel.Warning, $"Failed to save script configuration: {ex.Message}");
            throw;
        }
    }
}

public sealed class ScriptPlugin
    : IPostProcessorPlugin,
        IPluginCollectionSettingsProvider,
        IPluginCollectionItemTester,
        IPluginDataLocationAware,
        IPluginLocalizationAware
{
    private const string ScriptsCollectionKey = "scripts";

    private static readonly string[] s_allowedShells = ["", "bash", "sh", "pwsh"];

    // Long enough to read a result, short enough that a runaway script cannot flood the view.
    private const int TestPreviewCharacters = 4000;

    private IPluginHostServices? _host;
    private string? _dataDirectory;

    public string PluginId => "com.typewhisper.script";
    public string PluginName => "Script Runner";
    public string PluginVersion => PluginBuildInfo.Version;
    public string ProcessorName => "Script Runner";
    public int Priority => 400;

    public ScriptService? Service { get; private set; }

    /// <summary>Whether an executable is installed; replaced in tests.</summary>
    internal Func<string, bool> IsExecutableInstalled { get; init; } = ScriptShells.IsInstalled;

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        Service = new ScriptService(host);
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        Service = null;
        return Task.CompletedTask;
    }

    public async Task<string> ProcessAsync(
        string text,
        PostProcessingContext context,
        CancellationToken ct
    )
    {
        if (Service is null)
            return text;
        return await Service.RunScriptsAsync(text, context, ct);
    }

    public void SetDataDirectory(string pluginDataDirectory)
    {
        _dataDirectory = pluginDataDirectory;
    }

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions()
    {
        var itemFields = new PluginSettingDefinition[]
        {
            new("name", Loc.L("Settings.Name"), Kind: PluginSettingKind.Text),
            new("command", Loc.L("Settings.Command"), Kind: PluginSettingKind.Multiline),
            new(
                "shell",
                Loc.L("Settings.Shell"),
                Kind: PluginSettingKind.Dropdown,
                Options:
                [
                    new PluginSettingOption("", Loc.L("Settings.ShellBashDefault")),
                    new PluginSettingOption("bash", "bash"),
                    new PluginSettingOption("sh", "sh"),
                    new PluginSettingOption("pwsh", "PowerShell"),
                ]
            ),
            new("enabled", Loc.L("Settings.Enabled"), Kind: PluginSettingKind.Boolean),
            new("__id", "__id", Kind: PluginSettingKind.Text),
        };

        return
        [
            new PluginCollectionDefinition(
                ScriptsCollectionKey,
                Loc.L("Settings.Scripts"),
                Loc.L("Settings.ScriptsDescription"),
                itemFields,
                ItemLabelFieldKey: "name",
                AddButtonLabel: Loc.L("Settings.AddScript")
            )
            {
                Templates = GetTemplates(),
                ItemTest = new PluginCollectionItemTest(
                    Loc.L("Settings.TestScript"),
                    Loc.L("Settings.TestSampleInput")
                ),
            },
        ];
    }

    // Every template is bash, so none is offered on a system without it.
    private PluginCollectionTemplate[] GetTemplates() =>
        IsExecutableInstalled(ScriptTemplates.Shell)
            ? ScriptTemplates
                .All.Select(template =>
                {
                    var name = Loc.L($"Templates.{template.Id}.Name");
                    return new PluginCollectionTemplate(
                        template.Id,
                        name,
                        Loc.L($"Templates.{template.Id}.Description"),
                        new Dictionary<string, string?>
                        {
                            ["name"] = name,
                            ["command"] = template.Command,
                            ["shell"] = ScriptTemplates.Shell,
                            // Disabled until the user reviews the command and enables it.
                            ["enabled"] = "false",
                        }
                    );
                })
                .ToArray()
            : [];

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
        string collectionKey,
        CancellationToken ct = default
    )
    {
        if (!string.Equals(collectionKey, ScriptsCollectionKey, StringComparison.Ordinal))
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>([]);

        List<ScriptEntry> source;
        if (Service is not null)
        {
            source = Service.Scripts.ToList();
        }
        else
        {
            // ScriptStore.Load surfaces I/O / JSON errors so callers can decide
            // how to react; for settings retrieval we don't want a corrupt file
            // to break the screen — log and fall back to an empty list.
            try
            {
                source = new ScriptStore(ResolveDataDir()).Load();
            }
            catch (Exception ex)
            {
                _host?.Log(PluginLogLevel.Warning, $"Failed to load scripts: {ex.Message}");
                source = [];
            }
        }

        var items = source
            .Select(s => new PluginCollectionItem(
                new Dictionary<string, string?>
                {
                    ["name"] = s.Name,
                    ["command"] = s.Command,
                    // Surface legacy "powershell" values as "pwsh" so the
                    // dropdown matches the canonical option.
                    ["shell"] = ScriptService.NormalizeShellValue(s.Shell),
                    ["enabled"] = s.IsEnabled ? "true" : "false",
                    ["__id"] = s.Id.ToString("D"),
                }
            ))
            .ToList();

        return Task.FromResult<IReadOnlyList<PluginCollectionItem>>(items);
    }

    public Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default
    )
    {
        if (!string.Equals(collectionKey, ScriptsCollectionKey, StringComparison.Ordinal))
            return Task.FromResult(
                new PluginSettingsValidationResult(false, Loc.L("Settings.UnknownCollection"))
            );

        var entries = new List<ScriptEntry>(items.Count);

        foreach (var item in items)
        {
            var (entry, error) = ParseItem(item, requireName: true);
            if (entry is null)
                return Task.FromResult(new PluginSettingsValidationResult(false, error!));

            entries.Add(entry);
        }

        try
        {
            if (Service is not null)
            {
                Service.ReplaceAll(entries);
            }
            else
            {
                // Mirror ScriptService's _loadSucceeded safeguard: refuse to write
                // if the existing file fails to load, otherwise a corrupt/locked
                // scripts.json would be silently overwritten.
                var store = new ScriptStore(ResolveDataDir());
                try
                {
                    _ = store.Load();
                }
                catch (Exception ex)
                {
                    return Task.FromResult(
                        new PluginSettingsValidationResult(
                            false,
                            Loc.L("Settings.RefuseOverwriteScripts", ex.Message)
                        )
                    );
                }

                store.Save(entries);
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new PluginSettingsValidationResult(
                    false,
                    Loc.L("Settings.FailedToSaveScripts", ex.Message)
                )
            );
        }

        return Task.FromResult(new PluginSettingsValidationResult(true, Loc.L("Settings.Saved")));
    }

    public async Task<PluginCollectionItemTestResult> TestItemAsync(
        string collectionKey,
        PluginCollectionItem draft,
        string input,
        CancellationToken ct
    )
    {
        if (!string.Equals(collectionKey, ScriptsCollectionKey, StringComparison.Ordinal))
            return new PluginCollectionItemTestResult(false, Loc.L("Settings.UnknownCollection"));

        // Scripts only run while the plugin is enabled.
        if (Service is not { } service)
            return new PluginCollectionItemTestResult(false, Loc.L("Settings.TestRequiresEnabled"));

        // A test may run an unnamed draft.
        var (script, error) = ParseItem(draft, requireName: false);
        if (script is null)
            return new PluginCollectionItemTestResult(false, error!);

        var outcome = await service.RunTestAsync(script, input, ct);
        var stderr = Preview(outcome.StandardErrorText.Trim());
        return outcome.Status switch
        {
            ProcessRunStatus.StartFailed => new PluginCollectionItemTestResult(
                false,
                Loc.L(
                    "Settings.TestStartFailed",
                    ScriptService.ResolveShell(script).FileName,
                    outcome.StartError ?? ""
                )
            ),
            ProcessRunStatus.TimedOut => new PluginCollectionItemTestResult(
                false,
                Loc.L("Settings.TestTimedOut", ScriptService.Timeout.TotalSeconds),
                NullIfEmpty(stderr)
            ),
            ProcessRunStatus.OutputLimitExceeded => new PluginCollectionItemTestResult(
                false,
                Loc.L("Settings.TestOutputLimit", ScriptService.TestOutputLimitBytes / 1024)
            ),
            _ when outcome.ExitCode != 0 => new PluginCollectionItemTestResult(
                false,
                Loc.L("Settings.TestExitCode", outcome.ExitCode ?? -1),
                NullIfEmpty(stderr)
            ),
            _ => new PluginCollectionItemTestResult(
                true,
                Loc.L("Settings.TestSucceeded"),
                Preview(outcome.StandardOutputText)
            ),
        };
    }

    private static string Preview(string text) =>
        text.Length > TestPreviewCharacters ? text[..TestPreviewCharacters] + "…" : text;

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private (ScriptEntry? Entry, string? Error) ParseItem(PluginCollectionItem item, bool requireName)
    {
        item.Values.TryGetValue("name", out var rawName);
        item.Values.TryGetValue("command", out var rawCommand);
        item.Values.TryGetValue("shell", out var rawShell);

        var name = (rawName ?? "").Trim();
        var command = (rawCommand ?? "").Trim();
        var displayName = name.Length == 0 ? Loc.L("Settings.Unnamed") : name;

        if (requireName && name.Length == 0)
            return (null, Loc.L("Settings.ScriptNameRequired", displayName));

        if (command.Length == 0)
            return (null, Loc.L("Settings.ScriptCommandRequired", displayName));

        var shell = ScriptService.NormalizeShellValue(rawShell);
        if (Array.IndexOf(s_allowedShells, shell) < 0)
            return (null, Loc.L("Settings.ScriptUnknownShell", displayName, rawShell ?? ""));

        var id =
            item.Values.TryGetValue("__id", out var rawId) && Guid.TryParse(rawId, out var parsedId)
                ? parsedId
                : Guid.NewGuid();

        var isEnabled =
            !item.Values.TryGetValue("enabled", out var rawEnabled)
            || rawEnabled is null
            || !bool.TryParse(rawEnabled, out var parsedEnabled)
            || parsedEnabled;

        return (
            new ScriptEntry
            {
                Id = id,
                Name = name,
                Command = command,
                Shell = shell,
                IsEnabled = isEnabled,
            },
            null
        );
    }

    private string ResolveDataDir() =>
        _dataDirectory
        ?? throw new InvalidOperationException("Plugin data directory has not been set.");

    public void Dispose()
    {
        Service = null;
    }
}
