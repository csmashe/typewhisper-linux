using System.IO;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Obsidian;

public sealed partial class ObsidianPlugin : IActionPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private IPluginHostServices? _host;
    private List<ObsidianVaultInfo> _detectedVaults = [];

    public string PluginId => "com.typewhisper.obsidian";
    public string PluginName => "Obsidian";
    public string PluginVersion => "1.0.0";

    public string ActionId => "save-to-obsidian";
    public string ActionName => "Save to Obsidian";
    public string? ActionIcon => "\ud83d\udcdd";

    internal IPluginHostServices? Host => _host;
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
        _detectedVaults = DetectVaults();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    public async Task<ActionResult> ExecuteAsync(
        string input,
        ActionContext context,
        CancellationToken ct
    )
    {
        if (_host is null)
            return new ActionResult(false, Loc.L("Settings.PluginNotActivatedShort"));

        var vaultPath = _host.GetSetting<string>("vault-path");
        if (string.IsNullOrWhiteSpace(vaultPath))
            return new ActionResult(
                false,
                Loc.L("Settings.NoVaultConfigured")
            );

        if (!Directory.Exists(vaultPath))
            return new ActionResult(false, Loc.L("Settings.VaultPathNotFound", vaultPath));

        var subfolder = _host.GetSetting<string>("subfolder") ?? "TypeWhisper";
        var dailyNoteMode = _host.GetSetting<bool>("daily-note-mode");
        var filenameTemplate = _host.GetSetting<string>("filename-template");
        if (string.IsNullOrWhiteSpace(filenameTemplate))
            filenameTemplate = "{{date}} {{time}} Transcription";

        var now = DateTime.Now;
        var targetDir = Path.Join(vaultPath, subfolder);
        Directory.CreateDirectory(targetDir);

        string filePath;
        string filename;
        string content;

        if (dailyNoteMode)
        {
            filename = $"{now:yyyy-MM-dd}.md";
            filePath = Path.Join(targetDir, filename);

            var entry = BuildDailyNoteEntry(input, context, now);

            if (File.Exists(filePath))
            {
                await File.AppendAllTextAsync(filePath, entry, Encoding.UTF8, ct);
            }
            else
            {
                var header = $"# {now:yyyy-MM-dd}\n\n";
                await File.WriteAllTextAsync(filePath, header + entry, Encoding.UTF8, ct);
            }
        }
        else
        {
            filename = BuildFilename(filenameTemplate, context, now) + ".md";
            filePath = Path.Join(targetDir, filename);
            filePath = EnsureUniqueFilePath(filePath);
            filename = Path.GetFileName(filePath);

            content = BuildNoteContent(input, context, now);
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct);
        }

        _host.Log(PluginLogLevel.Info, $"Saved transcription to {filePath}");
        return new ActionResult(true, Loc.L("Settings.SavedTo", filename));
    }

    private static string BuildNoteContent(string input, ActionContext context, DateTime now)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine($"date: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("source: TypeWhisper");

        if (!string.IsNullOrEmpty(context.AppName))
            sb.AppendLine($"app: \"{EscapeYaml(context.AppName)}\"");

        if (!string.IsNullOrEmpty(context.Language))
            sb.AppendLine($"language: {context.Language}");

        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine(input);

        return sb.ToString();
    }

    private static string BuildDailyNoteEntry(string input, ActionContext context, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## {now:HH:mm}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(context.AppName))
            sb.AppendLine($"> Source: {context.AppName}");

        if (!string.IsNullOrEmpty(context.Language))
            sb.AppendLine($"> Language: {context.Language}");

        if (!string.IsNullOrEmpty(context.AppName) || !string.IsNullOrEmpty(context.Language))
            sb.AppendLine();

        sb.AppendLine(input);

        return sb.ToString();
    }

    private static string BuildFilename(string template, ActionContext context, DateTime now)
    {
        var filename = template
            .Replace("{{date}}", now.ToString("yyyy-MM-dd"))
            .Replace("{{time}}", now.ToString("HH-mm-ss"))
            .Replace("{{app}}", context.AppName ?? "Unknown");

        return SanitizeFilename(filename);
    }

    private static string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(filename.Length);

        foreach (var c in filename)
        {
            if (Array.IndexOf(invalid, c) >= 0)
                sanitized.Append('_');
            else
                sanitized.Append(c);
        }

        // Trailing dots/spaces are illegal on Windows; trim them so vaults
        // synced via Dropbox/OneDrive to a Windows host don't break.
        var result = sanitized.ToString().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "Transcription" : result;
    }

    private static string EnsureUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath))
            return filePath;

        var dir = Path.GetDirectoryName(filePath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var counter = 2;

        string candidate;
        do
        {
            candidate = Path.Join(dir, $"{nameWithoutExt} {counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    ///     Parses Obsidian's own obsidian.json so the vault picker can offer
    ///     real vaults instead of forcing a manual path. Best-effort: any
    ///     parse/IO failure just yields an empty list.
    /// </summary>
    internal static List<ObsidianVaultInfo> DetectVaults()
    {
        var vaults = new List<ObsidianVaultInfo>();

        try
        {
            var obsidianConfigPath = GetObsidianConfigPath();

            if (!File.Exists(obsidianConfigPath))
                return vaults;

            var json = File.ReadAllText(obsidianConfigPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("vaults", out var vaultsElement))
                return vaults;

            foreach (var vault in vaultsElement.EnumerateObject())
            {
                if (vault.Value.TryGetProperty("path", out var pathElement))
                {
                    var path = pathElement.GetString();
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        var name = Path.GetFileName(path);
                        vaults.Add(new ObsidianVaultInfo(name ?? vault.Name, path));
                    }
                }
            }
        }
        catch
        {
            // Silently ignore detection failures
        }

        return vaults;
    }

    private static string GetObsidianConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Join(appData, "obsidian", "obsidian.json");
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configHome = Path.Join(home, ".config");
        }

        return Path.Join(configHome, "obsidian", "obsidian.json");
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "vault-path",
                Loc.L("Settings.VaultPath"),
                Description: _detectedVaults.Count > 0
                    ? Loc.L("Settings.VaultPathDetectedDescription", _detectedVaults.Count)
                    : Loc.L("Settings.VaultPathDescription"),
                Placeholder: "/path/to/vault"
            ),
            new(
                "subfolder",
                Loc.L("Settings.Subfolder"),
                false,
                "TypeWhisper",
                Loc.L("Settings.SubfolderDescription")
            ),
            new(
                "daily-note-mode",
                Loc.L("Settings.SaveMode"),
                Description: Loc.L("Settings.SaveModeDescription"),
                Options:
                [
                    new PluginSettingOption("false", Loc.L("Settings.SaveModeOneNote")),
                    new PluginSettingOption("true", Loc.L("Settings.SaveModeDailyNote")),
                ]
            ),
            new(
                "filename-template",
                Loc.L("Settings.FilenameTemplate"),
                false,
                "{{date}} {{time}} Transcription",
                Loc.L("Settings.FilenameTemplateDescription")
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default)
    {
        if (_host is null)
            return Task.FromResult<string?>(null);

        return Task.FromResult(
            key switch
            {
                "vault-path" => _host.GetSetting<string>("vault-path"),
                "subfolder" => _host.GetSetting<string>("subfolder") ?? "TypeWhisper",
                "daily-note-mode" => _host.GetSetting<bool>("daily-note-mode") ? "true" : "false",
                "filename-template" => _host.GetSetting<string>("filename-template")
                    ?? "{{date}} {{time}} Transcription",
                _ => null,
            }
        );
    }

    public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        if (_host is null)
            return Task.CompletedTask;

        switch (key)
        {
            case "vault-path":
                _host.SetSetting("vault-path", value?.Trim() ?? string.Empty);
                break;
            case "subfolder":
                _host.SetSetting(
                    "subfolder",
                    string.IsNullOrWhiteSpace(value) ? "TypeWhisper" : value.Trim()
                );
                break;
            case "daily-note-mode":
                _host.SetSetting(
                    "daily-note-mode",
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                );
                break;
            case "filename-template":
                _host.SetSetting(
                    "filename-template",
                    string.IsNullOrWhiteSpace(value)
                        ? "{{date}} {{time}} Transcription"
                        : value.Trim()
                );
                break;
        }

        return Task.CompletedTask;
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (_host is null)
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, Loc.L("Settings.PluginNotActivated"))
            );

        var vaultPath = _host.GetSetting<string>("vault-path");
        if (string.IsNullOrWhiteSpace(vaultPath))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, Loc.L("Settings.EnterVaultPath"))
            );

        if (!Directory.Exists(vaultPath))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, Loc.L("Settings.VaultPathNotFound", vaultPath))
            );

        var obsidianDir = Path.Join(vaultPath, ".obsidian");
        return Task.FromResult<PluginSettingsValidationResult?>(
            Directory.Exists(obsidianDir)
                ? new PluginSettingsValidationResult(
                    true,
                    Loc.L("Settings.VaultDetected", Path.GetFileName(vaultPath))
                )
                : new PluginSettingsValidationResult(
                    false,
                    Loc.L("Settings.NotAnObsidianVault")
                )
        );
    }

    public void Dispose() { }
}

internal sealed record ObsidianVaultInfo(string Name, string Path);
