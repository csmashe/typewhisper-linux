using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Script;

public sealed record ScriptEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";

    /// <summary>
    /// Shell to run the command with. When empty, the OS-default shell is used.
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _dataDir;
    private readonly string _configPath;

    public ScriptStore(string dataDir)
    {
        _dataDir = dataDir;
        _configPath = Path.Combine(dataDir, "scripts.json");
    }

    public List<ScriptEntry> Load()
    {
        if (!File.Exists(_configPath)) return [];

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
                $"Failed to read script configuration from {_configPath}: {ex.Message}", ex);
        }
    }

    public void Save(IEnumerable<ScriptEntry> entries)
    {
        Directory.CreateDirectory(_dataDir);
        var json = JsonSerializer.Serialize(entries.ToList(), s_jsonOptions);
        File.WriteAllText(_configPath, json);
    }
}

public sealed class ScriptService
{
    private readonly IPluginHostServices _host;
    private readonly ScriptStore _store;

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
        if (script is not null)
        {
            Scripts.Remove(script);
            Save();
        }
    }

    public void UpdateScript(ScriptEntry updated)
    {
        for (var i = 0; i < Scripts.Count; i++)
        {
            if (Scripts[i].Id == updated.Id)
            {
                Scripts[i] = updated;
                Save();
                return;
            }
        }
    }

    public void MoveUp(Guid id)
    {
        var index = IndexOf(id);
        if (index > 0)
        {
            Scripts.Move(index, index - 1);
            Save();
        }
    }

    public void MoveDown(Guid id)
    {
        var index = IndexOf(id);
        if (index >= 0 && index < Scripts.Count - 1)
        {
            Scripts.Move(index, index + 1);
            Save();
        }
    }

    public async Task<string> RunScriptsAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        var current = text;

        foreach (var script in Scripts.ToList())
        {
            if (!script.IsEnabled) continue;

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

    private static (string FileName, string Arguments) ResolveShell(ScriptEntry script)
    {
        var shell = string.IsNullOrWhiteSpace(script.Shell)
            ? "bash"
            : script.Shell.Trim().ToLowerInvariant();

        return shell switch
        {
            "pwsh" or "powershell" => ("pwsh", $"-NoProfile -Command {script.Command}"),
            "sh" => ("sh", $"-c \"{script.Command.Replace("\"", "\\\"")}\""),
            // bash + any legacy/unknown value (e.g. an old "cmd" entry).
            _ => ("bash", $"-c \"{script.Command.Replace("\"", "\\\"")}\""),
        };
    }

    private async Task<string> RunSingleAsync(ScriptEntry script, string text, PostProcessingContext context, CancellationToken ct)
    {
        var (fileName, arguments) = ResolveShell(script);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.Environment["TYPEWHISPER_APP_NAME"] = context.ActiveAppName ?? "";
        psi.Environment["TYPEWHISPER_LANGUAGE"] = context.SourceLanguage ?? "";
        psi.Environment["TYPEWHISPER_PROFILE"] = context.ProfileName ?? "";

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Write text to stdin and close it so the script knows input is complete
        await process.StandardInput.WriteAsync(text);
        process.StandardInput.Close();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — kill the process and return original text
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _host.Log(PluginLogLevel.Warning, $"Script '{script.Name}' timed out after 5 seconds");
            return text;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _host.Log(PluginLogLevel.Warning,
                $"Script '{script.Name}' exited with code {process.ExitCode}: {stderr}");
            return text;
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            _host.Log(PluginLogLevel.Info,
                $"Script '{script.Name}' stderr: {stderr}");
        }

        return stdout;
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < Scripts.Count; i++)
            if (Scripts[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// Replaces the entire script collection with <paramref name="entries"/> and persists.
    /// </summary>
    public void ReplaceAll(IEnumerable<ScriptEntry> entries)
    {
        Scripts.Clear();
        foreach (var entry in entries)
            Scripts.Add(entry);
        Save();
    }

    private void Load()
    {
        try
        {
            foreach (var script in _store.Load())
                Scripts.Add(script);
        }
        catch (Exception ex)
        {
            _host.Log(PluginLogLevel.Warning, $"Failed to load script configuration: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            _store.Save(Scripts);
        }
        catch (Exception ex)
        {
            _host.Log(PluginLogLevel.Warning, $"Failed to save script configuration: {ex.Message}");
        }
    }
}

public sealed class ScriptPlugin : IPostProcessorPlugin, IPluginCollectionSettingsProvider, IPluginDataLocationAware
{
    private const string ScriptsCollectionKey = "scripts";

    private static readonly string[] s_allowedShells = ["", "bash", "sh", "pwsh"];

    private IPluginHostServices? _host;
    private string? _dataDirectory;

    public string PluginId => "com.typewhisper.script";
    public string PluginName => "Script Runner";
    public string PluginVersion => "1.0.0";
    public string ProcessorName => "Script Runner";
    public int Priority => 400;

    public ScriptService? Service { get; private set; }

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

    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        if (Service is null) return text;
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
            new("name", "Name", Kind: PluginSettingKind.Text),
            new("command", "Command", Kind: PluginSettingKind.Multiline),
            new("shell", "Shell", Kind: PluginSettingKind.Dropdown, Options:
            [
                new PluginSettingOption("", "OS default"),
                new PluginSettingOption("bash", "bash"),
                new PluginSettingOption("sh", "sh"),
                new PluginSettingOption("pwsh", "PowerShell"),
            ]),
            new("enabled", "Enabled", Kind: PluginSettingKind.Boolean),
            new("__id", "__id", Kind: PluginSettingKind.Text),
        };

        return
        [
            new PluginCollectionDefinition(
                ScriptsCollectionKey,
                "Scripts",
                "Shell scripts run on the transcript, in order.",
                itemFields,
                ItemLabelFieldKey: "name",
                AddButtonLabel: "Add script"),
        ];
    }

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(string collectionKey, CancellationToken ct = default)
    {
        if (!string.Equals(collectionKey, ScriptsCollectionKey, StringComparison.Ordinal))
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>([]);

        var source = Service is not null
            ? Service.Scripts.ToList()
            : new ScriptStore(ResolveDataDir()).Load();

        var items = source
            .Select(s => new PluginCollectionItem(new Dictionary<string, string?>
            {
                ["name"] = s.Name,
                ["command"] = s.Command,
                ["shell"] = s.Shell,
                ["enabled"] = s.IsEnabled ? "true" : "false",
                ["__id"] = s.Id.ToString("D"),
            }))
            .ToList();

        return Task.FromResult<IReadOnlyList<PluginCollectionItem>>(items);
    }

    public Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey, IReadOnlyList<PluginCollectionItem> items, CancellationToken ct = default)
    {
        if (!string.Equals(collectionKey, ScriptsCollectionKey, StringComparison.Ordinal))
            return Task.FromResult(new PluginSettingsValidationResult(false, "Unknown collection."));

        var entries = new List<ScriptEntry>(items.Count);

        foreach (var item in items)
        {
            item.Values.TryGetValue("name", out var rawName);
            item.Values.TryGetValue("command", out var rawCommand);
            item.Values.TryGetValue("shell", out var rawShell);

            var name = (rawName ?? "").Trim();
            var command = (rawCommand ?? "").Trim();
            var displayName = name.Length == 0 ? "(unnamed)" : name;

            if (name.Length == 0)
                return Task.FromResult(new PluginSettingsValidationResult(
                    false, $"Script '{displayName}': name is required."));

            if (command.Length == 0)
                return Task.FromResult(new PluginSettingsValidationResult(
                    false, $"Script '{displayName}': command is required."));

            var shell = (rawShell ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(s_allowedShells, shell) < 0)
                return Task.FromResult(new PluginSettingsValidationResult(
                    false, $"Script '{displayName}': unknown shell '{rawShell}'."));

            var id = item.Values.TryGetValue("__id", out var rawId) && Guid.TryParse(rawId, out var parsedId)
                ? parsedId
                : Guid.NewGuid();

            var isEnabled = !item.Values.TryGetValue("enabled", out var rawEnabled)
                || rawEnabled is null
                || !bool.TryParse(rawEnabled, out var parsedEnabled)
                || parsedEnabled;

            entries.Add(new ScriptEntry
            {
                Id = id,
                Name = name,
                Command = command,
                Shell = shell,
                IsEnabled = isEnabled,
            });
        }

        if (Service is not null)
            Service.ReplaceAll(entries);
        else
            new ScriptStore(ResolveDataDir()).Save(entries);

        return Task.FromResult(new PluginSettingsValidationResult(true, "Saved."));
    }

    private string ResolveDataDir()
        => _dataDirectory
           ?? throw new InvalidOperationException("Plugin data directory has not been set.");

    public void Dispose()
    {
        Service = null;
    }
}
