using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeWhisper.Core;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

internal sealed record SecretProtectionMigrationResult(
    int MigratedFileCount,
    int UnresolvedSecretCount,
    bool RootSettingsChanged,
    IReadOnlyList<string> Errors
)
{
    public bool HasUnresolvedSecrets => UnresolvedSecretCount > 0;
}

internal sealed class SecretProtectionMigrationService
{
    private const string SecretPrefix = "secret:";

    private static readonly string[] s_rootSecretProperties =
    [
        "groqApiKey",
        "openAiApiKey",
        "apiServerBearerToken",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _basePath;
    private readonly string _keyFilePath;

    public SecretProtectionMigrationService()
        : this(
            TypeWhisperEnvironment.BasePath,
            TypeWhisperEnvironment.SecretProtectionKeyFilePath
        )
    {
    }

    internal SecretProtectionMigrationService(string basePath, string? keyFilePath = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _keyFilePath = Path.GetFullPath(
            keyFilePath ?? Path.Join(_basePath, "secret-protection.key")
        );
    }

    public SecretProtectionMigrationResult MigrateAll()
    {
        try
        {
            return MigrateAllCore();
        }
        catch (Exception ex)
        {
            // Startup runs this before the UI exists, so an unexpected failure has to
            // fail closed (secrets stay unresolved, export stays blocked) rather than
            // take the launch down with it.
            Trace.WriteLine(
                $"[SecretProtectionMigration] Migration failed: {ex.Message}"
            );
            return new SecretProtectionMigrationResult(0, 1, false, [ex.Message]);
        }
    }

    private SecretProtectionMigrationResult MigrateAllCore()
    {
        try
        {
            var key = ApiKeyProtection.EnsureKeyFile(_keyFilePath);
            CryptographicOperations.ZeroMemory(key);
        }
        catch (Exception ex)
        {
            var unresolved = CountProtectedValues();
            Trace.WriteLine(
                $"[SecretProtectionMigration] Key validation failed: {ex.Message}"
            );
            return new SecretProtectionMigrationResult(
                0,
                unresolved,
                false,
                unresolved == 0 ? [] : [ex.Message]
            );
        }

        var migratedFiles = 0;
        var unresolvedSecrets = 0;
        var rootSettingsChanged = false;
        var errors = new List<string>();

        foreach (
            var path in new[]
            {
                Path.Join(_basePath, "settings.json"),
                Path.Join(_basePath, "settings.json.bak"),
            }
        )
        {
            var outcome = MigrateFileSafely(path, isRootSettings: true);
            migratedFiles += outcome.Migrated ? 1 : 0;
            unresolvedSecrets += outcome.UnresolvedSecretCount;
            if (outcome.Migrated && string.Equals(
                    path,
                    Path.Join(_basePath, "settings.json"),
                    StringComparison.Ordinal
                ))
            {
                rootSettingsChanged = true;
            }

            if (outcome.Error is not null)
            {
                errors.Add(outcome.Error);
            }
        }

        var pluginDataPath = Path.Join(_basePath, "PluginData");
        // ReSharper disable once InvertIf -- inverting would duplicate the result construction below.
        if (Directory.Exists(pluginDataPath))
        {
            foreach (var pluginDirectory in Directory.EnumerateDirectories(pluginDataPath))
            {
                var outcome = MigrateFileSafely(
                    Path.Join(pluginDirectory, "settings.json"),
                    isRootSettings: false
                );
                migratedFiles += outcome.Migrated ? 1 : 0;
                unresolvedSecrets += outcome.UnresolvedSecretCount;
                if (outcome.Error is not null)
                {
                    errors.Add(outcome.Error);
                }
            }
        }

        return new SecretProtectionMigrationResult(
            migratedFiles,
            unresolvedSecrets,
            rootSettingsChanged,
            errors
        );
    }

    private FileMigrationOutcome MigrateFileSafely(
        string path,
        bool isRootSettings
    )
    {
        try
        {
            return MigrateFile(path, isRootSettings);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[SecretProtectionMigration] Could not migrate '{path}': {ex.Message}"
            );
            return new FileMigrationOutcome(
                false,
                Math.Max(1, CountProtectedValues(path, isRootSettings)),
                $"Could not migrate protected settings in '{path}': {ex.Message}"
            );
        }
    }

    private FileMigrationOutcome MigrateFile(string path, bool isRootSettings)
    {
        if (!File.Exists(path))
        {
            return FileMigrationOutcome.None;
        }

        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new JsonException("The settings root must be a JSON object.");
        }
        catch (Exception ex) when (
            ex is IOException or JsonException or UnauthorizedAccessException
        )
        {
            Trace.WriteLine(
                $"[SecretProtectionMigration] Could not inspect '{path}': {ex.Message}"
            );
            return new FileMigrationOutcome(
                false,
                1,
                $"Could not inspect protected settings in '{path}': {ex.Message}"
            );
        }

        var protectedProperties = isRootSettings
            ? s_rootSecretProperties.Where(settings.ContainsKey)
            : settings
                .Select(property => property.Key)
                .Where(key => key.StartsWith(SecretPrefix, StringComparison.Ordinal));

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolved = 0;
        foreach (var propertyName in protectedProperties)
        {
            if (
                !settings.TryGetPropertyValue(propertyName, out var node)
                || node is null
            )
            {
                continue;
            }

            if (node is not JsonValue value || !value.TryGetValue<string>(out var stored))
            {
                unresolved++;
                continue;
            }

            if (string.IsNullOrEmpty(stored))
            {
                continue;
            }

            var result = ApiKeyProtection.Decrypt(stored, _keyFilePath);
            if (result.Format == SecretProtectionFormat.Current)
            {
                continue;
            }

            if (result is { Succeeded: true, PlainText: not null })
            {
                replacements[propertyName] = ApiKeyProtection.Encrypt(
                    result.PlainText,
                    _keyFilePath
                );
                continue;
            }

            if (
                isRootSettings
                && string.Equals(
                    propertyName,
                    "apiServerBearerToken",
                    StringComparison.Ordinal
                )
            )
            {
                replacements[propertyName] = ApiKeyProtection.Encrypt(
                    CreateBearerToken(),
                    _keyFilePath
                );
                continue;
            }

            unresolved++;
        }

        if (unresolved > 0)
        {
            return new FileMigrationOutcome(false, unresolved, null);
        }

        if (replacements.Count == 0)
        {
            return FileMigrationOutcome.None;
        }

        foreach (var replacement in replacements)
        {
            settings[replacement.Key] = replacement.Value;
        }

        try
        {
            AtomicFileWrite.WriteAllText(path, settings.ToJsonString(s_jsonOptions));
            return new FileMigrationOutcome(true, 0, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(
                $"[SecretProtectionMigration] Could not replace '{path}': {ex.Message}"
            );
            return new FileMigrationOutcome(
                false,
                replacements.Count,
                $"Could not migrate protected settings in '{path}': {ex.Message}"
            );
        }
    }

    private int CountProtectedValues()
    {
        var count = CountProtectedValues(
                Path.Join(_basePath, "settings.json"),
                isRootSettings: true
            )
            + CountProtectedValues(
                Path.Join(_basePath, "settings.json.bak"),
                isRootSettings: true
            );

        var pluginDataPath = Path.Join(_basePath, "PluginData");
        if (!Directory.Exists(pluginDataPath))
        {
            return count;
        }

        count += Directory
            .EnumerateDirectories(pluginDataPath)
            .Sum(pluginDirectory =>
                CountProtectedValues(
                    Path.Join(pluginDirectory, "settings.json"),
                    isRootSettings: false
                )
            );

        return count;
    }

    private static int CountProtectedValues(string path, bool isRootSettings)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject settings)
            {
                return 1;
            }

            return isRootSettings
                ? s_rootSecretProperties.Count(property =>
                    HasNonEmptyValue(settings, property)
                )
                : settings.Count(property =>
                    property.Key.StartsWith(SecretPrefix, StringComparison.Ordinal)
                    && HasNonEmptyValue(settings, property.Key)
                );
        }
        catch (Exception ex) when (
            ex is IOException or JsonException or UnauthorizedAccessException
        )
        {
            return 1;
        }
    }

    private static bool HasNonEmptyValue(JsonObject settings, string propertyName)
    {
        return settings.TryGetPropertyValue(propertyName, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var stored)
            && !string.IsNullOrEmpty(stored);
    }

    private static string CreateBearerToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private readonly record struct FileMigrationOutcome(
        bool Migrated,
        int UnresolvedSecretCount,
        string? Error
    )
    {
        public static FileMigrationOutcome None => new(false, 0, null);
    }
}
