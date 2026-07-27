// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.Obsidian;

public sealed class ObsidianPlugin : IActionPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const int MaxIndividualNotePathAttempts = 10_000;
    private const int UnixFileExistsError = 17;
    private const int WindowsFileExistsError = 80;
    private const int WindowsAlreadyExistsError = 183;

    private readonly Func<List<ObsidianVaultInfo>> _detectVaults;
    private List<ObsidianVaultInfo> _detectedVaults = [];

    public ObsidianPlugin() : this(DetectVaults) { }

    internal ObsidianPlugin(Func<List<ObsidianVaultInfo>> detectVaults)
    {
        ArgumentNullException.ThrowIfNull(detectVaults);
        _detectVaults = detectVaults;
    }

    public string PluginId => "com.typewhisper.obsidian";
    public string PluginName => "Obsidian";
    public string PluginVersion => "1.0.0";

    public string ActionId => "save-to-obsidian";
    public string ActionName => "Save to Obsidian";
    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? ActionIcon => "\ud83d\udcdd";

    internal IPluginHostServices? Host { get; private set; }

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => Host?.Localization ?? _injectedLocalization;

    public Task ActivateAsync(IPluginHostServices host)
    {
        Host = host;
        _detectedVaults = _detectVaults();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    public async Task<ActionResult> ExecuteAsync(
        string input,
        ActionContext context,
        CancellationToken ct
    )
    {
        if (Host is null)
            return new ActionResult(false, Loc.L("Settings.PluginNotActivatedShort"));

        var vaultPath = Host.GetSetting<string>("vault-path");
        if (string.IsNullOrWhiteSpace(vaultPath))
            return new ActionResult(
                false,
                Loc.L("Settings.NoVaultConfigured")
            );

        if (!Directory.Exists(vaultPath))
            return new ActionResult(false, Loc.L("Settings.VaultPathNotFound", vaultPath));

        var subfolder = Host.GetSetting<string>("subfolder") ?? "TypeWhisper";
        var dailyNoteMode = Host.GetSetting<bool>("daily-note-mode");
        var filenameTemplate = Host.GetSetting<string>("filename-template");
        if (string.IsNullOrWhiteSpace(filenameTemplate))
            filenameTemplate = "{{date}} {{time}} Transcription";

        var now = DateTime.Now;
        var targetDir = Path.Join(vaultPath, subfolder);
        Directory.CreateDirectory(targetDir);

        string filePath;
        string filename;

        if (dailyNoteMode)
        {
            filename = $"{now:yyyy-MM-dd}.md";
            filePath = Path.Join(targetDir, filename);

            var entry = BuildDailyNoteEntry(input, context, now);
            var header = $"# {now:yyyy-MM-dd}\n\n";
            var lockPath = GetDailyNoteLockPath(Host.PluginDataDirectory, filePath);
            await WriteDailyNoteAsync(filePath, lockPath, header, entry, ct);
        }
        else
        {
            filename = BuildFilename(filenameTemplate, context, now) + ".md";
            filePath = Path.Join(targetDir, filename);

            var content = BuildNoteContent(input, context, now);
            filePath = await WriteIndividualNoteAsync(filePath, content, ct);
            filename = Path.GetFileName(filePath);
        }

        Host.Log(PluginLogLevel.Info, $"Saved transcription to {filePath}");
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
            // ReSharper disable once ConvertIfStatementToConditionalTernaryExpression -- subjective style; kept as an explicit if.
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

    private static Task<string> WriteIndividualNoteAsync(
        string filePath,
        string content,
        CancellationToken ct
    ) =>
        WriteIndividualNoteAsync(filePath, content, WriteUtf8TextAsync, ct);

    internal static async Task<string> WriteIndividualNoteAsync(
        string filePath,
        string content,
        Func<FileStream, string, CancellationToken, Task> writeAsync,
        CancellationToken ct
    )
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);

        for (var attempt = 0; attempt < MaxIndividualNotePathAttempts; attempt++)
        {
            var candidate = attempt == 0
                ? filePath
                : Path.Join(dir, $"{nameWithoutExt} {attempt + 1}{ext}");
            FileStream claimedStream;

            try
            {
                claimedStream = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous
                );
            }
            catch (IOException ex) when (IsCreateNewCollision(ex))
            {
                continue;
            }

            try
            {
                await using (claimedStream)
                {
                    await writeAsync(claimedStream, content, ct);
                    await claimedStream.FlushAsync(ct);
                }

                return candidate;
            }
            catch
            {
                TryDeleteOwnedFile(candidate);
                throw;
            }
        }

        throw new IOException(
            $"Could not create a unique Obsidian note after {MaxIndividualNotePathAttempts} attempts."
        );
    }

    internal static string GetDailyNoteLockPath(string pluginDataDirectory, string notePath)
    {
        var normalizedNotePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(notePath));
        if (OperatingSystem.IsWindows())
            normalizedNotePath = normalizedNotePath.ToUpperInvariant();

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedNotePath))
        );
        return Path.Join(pluginDataDirectory, "locks", $"{hash}.lock");
    }

    private static async Task WriteDailyNoteAsync(
        string filePath,
        string lockPath,
        string header,
        string entry,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        await using (await InterProcessFileLock.AcquireAsync(lockPath, ct))
        {
            if (File.Exists(filePath))
            {
                // The sentinel already serializes TypeWhisper writers, so allow
                // read sharing: an editor/sync client holding the note open for
                // reading must not turn this append into a sharing violation.
                var originalLength = new FileInfo(filePath).Length;
                try
                {
                    await using var appendStream = new FileStream(
                        filePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.Asynchronous
                    );
                    await WriteUtf8TextAsync(appendStream, entry, ct);
                    await appendStream.FlushAsync(ct);
                }
                catch
                {
                    // Roll the note back to its pre-append length so a failed or
                    // cancelled write leaves no partial entry behind.
                    TryTruncateFile(filePath, originalLength);
                    throw;
                }

                return;
            }

            FileStream claimedStream = new(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous
            );

            try
            {
                await using (claimedStream)
                {
                    await WriteUtf8TextAsync(claimedStream, header + entry, ct);
                    await claimedStream.FlushAsync(ct);
                }
            }
            catch
            {
                TryDeleteOwnedFile(filePath);
                throw;
            }
        }
    }

    private static async Task WriteUtf8TextAsync(
        FileStream stream,
        string content,
        CancellationToken ct
    )
    {
        await using var writer = new StreamWriter(
            stream,
            Encoding.UTF8,
            bufferSize: 1024,
            leaveOpen: true
        );
        await writer.WriteAsync(content.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    private static bool IsCreateNewCollision(IOException exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is
            UnixFileExistsError
            or WindowsFileExistsError
            or WindowsAlreadyExistsError;
    }

    private static void TryDeleteOwnedFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the original cancellation/write failure if best-effort cleanup fails.
        }
    }

    private static void TryTruncateFile(string path, long length)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None
            );
            if (stream.Length > length)
                stream.SetLength(length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the original cancellation/write failure if best-effort cleanup fails.
        }
    }

    // ReSharper disable once UseVerbatimString -- the mixed backslash/quote escapes read no better as a verbatim string.
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
                // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                if (vault.Value.TryGetProperty("path", out var pathElement))
                {
                    var path = pathElement.GetString();
                    // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        // Path.GetFileName yields "" for a trailing-separator path; trim
                        // separators first, then fall back to the vault key so the picker
                        // never shows a blank display name.
                        var name = Path.GetFileName(
                            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (string.IsNullOrEmpty(name))
                            name = vault.Name;
                        vaults.Add(new ObsidianVaultInfo(name, path));
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
        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
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
        if (Host is null)
            return Task.FromResult<string?>(null);

        return Task.FromResult(
            key switch
            {
                "vault-path" => Host.GetSetting<string>("vault-path"),
                "subfolder" => Host.GetSetting<string>("subfolder") ?? "TypeWhisper",
                "daily-note-mode" => Host.GetSetting<bool>("daily-note-mode") ? "true" : "false",
                "filename-template" => Host.GetSetting<string>("filename-template")
                    ?? "{{date}} {{time}} Transcription",
                _ => null,
            }
        );
    }

    public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        if (Host is null)
            return Task.CompletedTask;

        switch (key)
        {
            case "vault-path":
                Host.SetSetting("vault-path", value?.Trim() ?? string.Empty);
                break;
            case "subfolder":
                Host.SetSetting(
                    "subfolder",
                    string.IsNullOrWhiteSpace(value) ? "TypeWhisper" : value.Trim()
                );
                break;
            case "daily-note-mode":
                Host.SetSetting(
                    "daily-note-mode",
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                );
                break;
            case "filename-template":
                Host.SetSetting(
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
        if (Host is null)
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, Loc.L("Settings.PluginNotActivated"))
            );

        var vaultPath = Host.GetSetting<string>("vault-path");
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
