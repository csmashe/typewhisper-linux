using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private const int QuarantineVersion = 1;
    private const int RetirementFailureThreshold = 3;
    private const string SecretPrefix = "secret:";
    private const string QuarantineFileName = "retired-provider-secrets.quarantine.json";

    private const UnixFileMode QuarantineFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly string s_processStartupId = Guid.NewGuid().ToString("N");

    private static readonly string[] s_rootSecretProperties =
    [
        "groqApiKey",
        "openAiApiKey",
        "apiServerBearerToken",
    ];

    private static readonly string[] s_retiredProviderProperties =
    [
        "groqApiKey",
        "openAiApiKey",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _basePath;
    private readonly string _keyFilePath;
    private readonly Action? _quarantinePersistedObserver;
    private readonly AtomicJsonStore<QuarantineDocument> _quarantineStore;
    private readonly string _quarantinePath;
    private readonly string _startupId;

    public SecretProtectionMigrationService()
        : this(
            TypeWhisperEnvironment.BasePath,
            TypeWhisperEnvironment.SecretProtectionKeyFilePath
        )
    {
    }

    internal SecretProtectionMigrationService(
        string basePath,
        string? keyFilePath = null,
        string? startupId = null,
        Action? quarantinePersistedObserver = null
    )
    {
        _basePath = Path.GetFullPath(basePath);
        _keyFilePath = Path.GetFullPath(
            keyFilePath ?? Path.Join(_basePath, "secret-protection.key")
        );
        _startupId = startupId ?? s_processStartupId;
        _quarantinePersistedObserver = quarantinePersistedObserver;
        _quarantinePath = Path.Join(_basePath, QuarantineFileName);
        _quarantineStore = new AtomicJsonStore<QuarantineDocument>(
            _quarantinePath,
            static () => new QuarantineDocument(),
            new AtomicJsonStoreOptions<QuarantineDocument>
            {
                JsonOptions = s_jsonOptions,
                BackupMode = AtomicJsonBackupMode.None,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.Throw,
                Deserialize = DeserializeQuarantine,
            }
        );
    }

    // The backup export guards this file's inode the same way as the key: its
    // ciphertext is protected only by a derivation from guessable inputs.
    internal string QuarantinePath => _quarantinePath;

    public SecretProtectionMigrationResult MigrateAll()
    {
        return MigrateAll(countStartupFailures: false);
    }

    public SecretProtectionMigrationResult MigrateAllAtStartup()
    {
        return MigrateAll(countStartupFailures: true);
    }

    private SecretProtectionMigrationResult MigrateAll(bool countStartupFailures)
    {
        try
        {
            return MigrateAllCore(countStartupFailures);
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

    private SecretProtectionMigrationResult MigrateAllCore(bool countStartupFailures)
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
            var outcome = MigrateFileSafely(
                path,
                isRootSettings: true,
                countStartupFailures
            );
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
                    isRootSettings: false,
                    countStartupFailures
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
        bool isRootSettings,
        bool countStartupFailures
    )
    {
        try
        {
            return MigrateFile(path, isRootSettings, countStartupFailures);
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

    private FileMigrationOutcome MigrateFile(
        string path,
        bool isRootSettings,
        bool countStartupFailures
    )
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

        var replacements = new Dictionary<string, SecretReplacement>(StringComparer.Ordinal);
        var sourceChanged = false;
        var unresolved = 0;
        string? migrationError = null;
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
                RemoveFailureAtStartup(path, propertyName, countStartupFailures);
                continue;
            }

            if (result is { Succeeded: true, PlainText: not null })
            {
                RemoveFailureAtStartup(path, propertyName, countStartupFailures);
                replacements[propertyName] = new SecretReplacement(
                    stored,
                    ApiKeyProtection.Encrypt(result.PlainText, _keyFilePath)
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
                replacements[propertyName] = new SecretReplacement(
                    stored,
                    ApiKeyProtection.Encrypt(CreateBearerToken(), _keyFilePath)
                );
                continue;
            }

            if (
                countStartupFailures
                && isRootSettings
                && s_retiredProviderProperties.Contains(
                    propertyName,
                    StringComparer.Ordinal
                )
            )
            {
                try
                {
                    var hasDurableQuarantineCopy = RecordFailedProviderSecret(
                        path,
                        propertyName,
                        stored
                    );
                    if (hasDurableQuarantineCopy)
                    {
                        _quarantinePersistedObserver?.Invoke();
                        if (CompareAndSet(path, propertyName, stored, ""))
                        {
                            sourceChanged = true;
                            continue;
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is IOException
                        or JsonException
                        or UnauthorizedAccessException
                )
                {
                    Trace.WriteLine(
                        $"[SecretProtectionMigration] Could not quarantine '{path}' property "
                            + $"'{propertyName}' at '{_quarantinePath}': {ex.Message}"
                    );
                    migrationError ??=
                        $"Could not quarantine protected settings in '{path}': {ex.Message}";
                }
            }

            unresolved++;
        }

        if (unresolved > 0)
        {
            return new FileMigrationOutcome(sourceChanged, unresolved, migrationError);
        }

        if (replacements.Count == 0)
        {
            return new FileMigrationOutcome(sourceChanged, 0, migrationError);
        }

        try
        {
            if (!ApplyReplacements(path, replacements))
            {
                return new FileMigrationOutcome(sourceChanged, replacements.Count, null);
            }

            return new FileMigrationOutcome(true, 0, null);
        }
        catch (Exception ex) when (
            ex is IOException or JsonException or UnauthorizedAccessException
        )
        {
            Trace.WriteLine(
                $"[SecretProtectionMigration] Could not replace '{path}': {ex.Message}"
            );
            return new FileMigrationOutcome(
                sourceChanged,
                replacements.Count,
                $"Could not migrate protected settings in '{path}': {ex.Message}"
            );
        }
    }

    private void RemoveFailureAtStartup(
        string path,
        string propertyName,
        bool countStartupFailures
    )
    {
        if (
            !countStartupFailures
            || !s_retiredProviderProperties.Contains(
                propertyName,
                StringComparer.Ordinal
            )
            || !File.Exists(_quarantinePath)
        )
        {
            return;
        }

        var sourceFile = Path.GetRelativePath(_basePath, path);
        try
        {
            PrepareQuarantineMode();
            _quarantineStore.Update(current =>
            {
                var failures = current.Failures
                    .Where(failure =>
                        !string.Equals(
                            failure.SourceFile,
                            sourceFile,
                            StringComparison.Ordinal
                        )
                        || !string.Equals(
                            failure.Property,
                            propertyName,
                            StringComparison.Ordinal
                        )
                    )
                    .ToList();
                return failures.Count == current.Failures.Count
                    ? current
                    : current with { Failures = failures };
            });
            VerifyQuarantineMode();
        }
        catch (Exception ex) when (
            ex is IOException or JsonException or UnauthorizedAccessException
        )
        {
            Trace.WriteLine(
                $"[SecretProtectionMigration] Could not reset provider failure state at "
                    + $"'{_quarantinePath}': {ex.Message}"
            );
        }
    }

    private bool RecordFailedProviderSecret(
        string path,
        string propertyName,
        string ciphertext
    )
    {
        var sourceFile = Path.GetRelativePath(_basePath, path);
        var ciphertextHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext))
        );
        var hasDurableQuarantineCopy = false;

        PrepareQuarantineMode();
        var committed = _quarantineStore.Update(current =>
        {
            var failures = current.Failures.ToList();
            var existingIndex = failures.FindIndex(failure =>
                string.Equals(
                    failure.SourceFile,
                    sourceFile,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    failure.Property,
                    propertyName,
                    StringComparison.Ordinal
                )
            );
            var existing = existingIndex >= 0 ? failures[existingIndex] : null;
            var sameCiphertext = existing is not null
                && string.Equals(
                    existing.CiphertextHash,
                    ciphertextHash,
                    StringComparison.Ordinal
                );
            var sameRun = sameCiphertext
                && string.Equals(
                    existing!.LastStartupId,
                    _startupId,
                    StringComparison.Ordinal
                );
            var failureCount = sameRun
                ? existing!.FailureCount
                : sameCiphertext
                    ? existing!.FailureCount + 1
                    : 1;
            var timestamp = sameRun
                ? existing!.LastFailureAtUtc
                : DateTimeOffset.UtcNow;
            var failure = new ProviderSecretFailure(
                sourceFile,
                propertyName,
                ciphertextHash,
                failureCount,
                _startupId,
                timestamp
            );
            if (existingIndex >= 0)
            {
                failures[existingIndex] = failure;
            }
            else
            {
                failures.Add(failure);
            }

            var retiredSecrets = current.RetiredSecrets.ToList();
            var retired = retiredSecrets.FirstOrDefault(secret =>
                string.Equals(
                    secret.SourceFile,
                    sourceFile,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    secret.Property,
                    propertyName,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    secret.CiphertextHash,
                    ciphertextHash,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    secret.Ciphertext,
                    ciphertext,
                    StringComparison.Ordinal
                )
            );
            if (failureCount >= RetirementFailureThreshold && retired is null)
            {
                // Manual recovery: restore the original HOME/username, copy Ciphertext back into
                // settings.json, and restart; the existing migration will upgrade it to v2. If the
                // original environment is NOT restored, the retained retired entry re-clears the
                // pasted ciphertext on the first failed restart (no fresh three-run grace) — the
                // quarantine copy persists either way, so recovery can be retried.
                retired = new RetiredProviderSecret(
                    sourceFile,
                    propertyName,
                    ciphertext,
                    ciphertextHash,
                    failureCount,
                    _startupId,
                    timestamp
                );
                retiredSecrets.Add(retired);
            }

            hasDurableQuarantineCopy = retired is not null;
            return current with
            {
                Failures = failures,
                RetiredSecrets = retiredSecrets,
            };
        });
        VerifyQuarantineMode();

        if (
            hasDurableQuarantineCopy
            && committed.RetiredSecrets.Any(secret =>
                string.Equals(
                    secret.SourceFile,
                    sourceFile,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    secret.Property,
                    propertyName,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    secret.Ciphertext,
                    ciphertext,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    secret.CiphertextHash,
                    ciphertextHash,
                    StringComparison.Ordinal
                )
            )
        )
        {
            Trace.WriteLine(
                $"[SecretProtectionMigration] Retired provider secret quarantine: "
                    + $"'{_quarantinePath}'"
            );
            return true;
        }

        return false;
    }

    private static bool ApplyReplacements(
        string path,
        IReadOnlyDictionary<string, SecretReplacement> replacements
    )
    {
        var store = CreateSettingsStore(path);
        var applied = false;
        store.Update(current =>
        {
            if (
                replacements.Any(replacement =>
                    !HasExactString(
                        current,
                        replacement.Key,
                        replacement.Value.Expected
                    )
                )
            )
            {
                return current;
            }

            foreach (var replacement in replacements)
            {
                current[replacement.Key] = replacement.Value.Replacement;
            }

            applied = true;
            return current;
        });
        return applied;
    }

    private static bool CompareAndSet(
        string path,
        string propertyName,
        string expected,
        string replacement
    )
    {
        var store = CreateSettingsStore(path);
        var applied = false;
        store.Update(current =>
        {
            if (!HasExactString(current, propertyName, expected))
            {
                return current;
            }

            current[propertyName] = replacement;
            applied = true;
            return current;
        });
        return applied;
    }

    private static AtomicJsonStore<JsonObject> CreateSettingsStore(string path)
    {
        return new AtomicJsonStore<JsonObject>(
            path,
            static () => new JsonObject(),
            new AtomicJsonStoreOptions<JsonObject>
            {
                BackupMode = AtomicJsonBackupMode.None,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.Throw,
                Deserialize = static json =>
                    JsonNode.Parse(json) as JsonObject
                    ?? throw new JsonException(
                        "The settings root must be a JSON object."
                    ),
                Serialize = static settings =>
                    settings.ToJsonString(s_jsonOptions),
            }
        );
    }

    private static bool HasExactString(
        JsonObject settings,
        string propertyName,
        string expected
    )
    {
        return settings.TryGetPropertyValue(propertyName, out var node)
            && node is JsonValue value
            && value.TryGetValue<string>(out var stored)
            && string.Equals(stored, expected, StringComparison.Ordinal);
    }

    private void PrepareQuarantineMode()
    {
        if (File.Exists(_quarantinePath) && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_quarantinePath, QuarantineFileMode);
        }
    }

    private void VerifyQuarantineMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(_quarantinePath, QuarantineFileMode);
        if (File.GetUnixFileMode(_quarantinePath) != QuarantineFileMode)
        {
            throw new IOException(
                $"Could not apply Unix mode '{QuarantineFileMode}' to "
                    + $"'{_quarantinePath}'."
            );
        }
    }

    private static QuarantineDocument DeserializeQuarantine(string json)
    {
        var document = JsonSerializer.Deserialize<QuarantineDocument>(
                json,
                s_jsonOptions
            )
            ?? throw new JsonException("The quarantine document is empty.");
        if (document.Version != QuarantineVersion)
        {
            throw new JsonException(
                $"Unsupported retired provider secret quarantine version "
                    + $"'{document.Version}'."
            );
        }

        if (document.Failures is null || document.RetiredSecrets is null)
        {
            throw new JsonException("The quarantine document is incomplete.");
        }

        return document;
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

    private sealed record QuarantineDocument
    {
        public int Version { get; init; } = QuarantineVersion;

        public List<ProviderSecretFailure> Failures { get; init; } = [];

        public List<RetiredProviderSecret> RetiredSecrets { get; init; } = [];
    }

    private sealed record ProviderSecretFailure(
        string SourceFile,
        string Property,
        string CiphertextHash,
        int FailureCount,
        string LastStartupId,
        DateTimeOffset LastFailureAtUtc
    );

    // Serialized quarantine-document carrier: FailureCount/StartupId/TimestampUtc are recovery
    // metadata written to disk via System.Text.Json, not read back in code.
    // ReSharper disable NotAccessedPositionalProperty.Local
    private sealed record RetiredProviderSecret(
        string SourceFile,
        string Property,
        string Ciphertext,
        string CiphertextHash,
        int FailureCount,
        string StartupId,
        DateTimeOffset TimestampUtc
    );

    // ReSharper restore NotAccessedPositionalProperty.Local

    private readonly record struct SecretReplacement(
        string Expected,
        string Replacement
    );

    private readonly record struct FileMigrationOutcome(
        bool Migrated,
        int UnresolvedSecretCount,
        string? Error
    )
    {
        public static FileMigrationOutcome None => new(false, 0, null);
    }
}
