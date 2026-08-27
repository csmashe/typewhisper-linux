using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class SecretProtectionMigrationServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _basePath;
    private readonly string _keyFilePath;

    public SecretProtectionMigrationServiceTests()
    {
        _basePath = TestPaths.CreateTempDirectory(
            "TypeWhisper.SecretProtectionMigrationServiceTests"
        );
        _keyFilePath = Path.Join(_basePath, "secret-protection.key");
    }

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_basePath);
    }

    [Fact]
    public void MigrateAll_UpgradesRootBackupAndPluginLegacyGcmValuesToV2()
    {
        var rootLegacy = ApiKeyProtectionTests.EncryptLegacyGcm("root-secret");
        var backupLegacy = ApiKeyProtectionTests.EncryptLegacyGcm("backup-secret");
        var pluginLegacy = ApiKeyProtectionTests.EncryptLegacyGcm("plugin-secret");
        WriteJson(
            Path.Join(_basePath, "settings.json"),
            new Dictionary<string, object?>
            {
                ["groqApiKey"] = rootLegacy,
                ["apiServerBearerToken"] = "legacy-plaintext-token",
            }
        );
        WriteJson(
            Path.Join(_basePath, "settings.json.bak"),
            new Dictionary<string, object?> { ["openAiApiKey"] = backupLegacy }
        );
        var pluginSettingsPath = Path.Join(
            _basePath,
            "PluginData",
            "com.test.plugin",
            "settings.json"
        );
        WriteJson(
            pluginSettingsPath,
            new Dictionary<string, object?>
            {
                ["language"] = "en",
                ["secret:api-key"] = pluginLegacy,
            }
        );

        var result = CreateService().MigrateAll();

        Assert.Equal(3, result.MigratedFileCount);
        Assert.Equal(0, result.UnresolvedSecretCount);
        Assert.True(result.RootSettingsChanged);
        AssertCurrentSecret(
            ReadString(Path.Join(_basePath, "settings.json"), "groqApiKey"),
            "root-secret"
        );
        AssertCurrentSecret(
            ReadString(
                Path.Join(_basePath, "settings.json"),
                "apiServerBearerToken"
            ),
            "legacy-plaintext-token"
        );
        AssertCurrentSecret(
            ReadString(
                Path.Join(_basePath, "settings.json.bak"),
                "openAiApiKey"
            ),
            "backup-secret"
        );
        AssertCurrentSecret(
            ReadString(pluginSettingsPath, "secret:api-key"),
            "plugin-secret"
        );
        Assert.NotEqual(rootLegacy, ReadString(
            Path.Join(_basePath, "settings.json"),
            "groqApiKey"
        ));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_keyFilePath)
        );

        var primaryBytes = File.ReadAllBytes(Path.Join(_basePath, "settings.json"));
        var secondResult = CreateService().MigrateAll();
        Assert.Equal(0, secondResult.MigratedFileCount);
        Assert.Equal(
            primaryBytes,
            File.ReadAllBytes(Path.Join(_basePath, "settings.json"))
        );
    }

    [Fact]
    public void MigrateAll_OneUndecryptableSecretLeavesWholeFileUnchanged()
    {
        var valid = ApiKeyProtectionTests.EncryptLegacyGcm("valid-secret");
        var invalidBytes = Convert.FromBase64String(
            ApiKeyProtectionTests.EncryptLegacyGcm("invalid-secret")
        );
        invalidBytes[^1] ^= 0x20;
        var settingsPath = Path.Join(_basePath, "settings.json");
        WriteJson(
            settingsPath,
            new Dictionary<string, object?>
            {
                ["groqApiKey"] = valid,
                ["openAiApiKey"] = Convert.ToBase64String(invalidBytes),
            }
        );
        var before = File.ReadAllBytes(settingsPath);

        var result = CreateService().MigrateAll();

        Assert.Equal(0, result.MigratedFileCount);
        Assert.Equal(1, result.UnresolvedSecretCount);
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
    }

    [Fact]
    public void MigrateAll_V2FromDifferentInstallationFailsClosedAndPreservesCiphertext()
    {
        var foreignDirectory = TestPaths.CreateTempDirectory(
            "TypeWhisper.SecretProtectionMigrationForeignKey"
        );
        try
        {
            var foreignKeyPath = Path.Join(foreignDirectory, "foreign.key");
            var foreignCiphertext = ApiKeyProtection.Encrypt(
                "foreign-secret",
                foreignKeyPath
            );
            ApiKeyProtection.Encrypt("local-seed", _keyFilePath);
            var pluginSettingsPath = Path.Join(
                _basePath,
                "PluginData",
                "com.test.plugin",
                "settings.json"
            );
            WriteJson(
                pluginSettingsPath,
                new Dictionary<string, object?>
                {
                    ["secret:api-key"] = foreignCiphertext,
                }
            );
            var before = File.ReadAllBytes(pluginSettingsPath);

            var result = CreateService().MigrateAll();

            Assert.Equal(1, result.UnresolvedSecretCount);
            Assert.Equal(before, File.ReadAllBytes(pluginSettingsPath));
            Assert.Equal(
                foreignCiphertext,
                ReadString(pluginSettingsPath, "secret:api-key")
            );
        }
        finally
        {
            TestPaths.DeleteDirectory(foreignDirectory);
        }
    }

    [Fact]
    public void MigrateAll_MalformedKeyDoesNotChangeLegacySettings()
    {
        File.WriteAllBytes(_keyFilePath, RandomNumberGenerator.GetBytes(31));
        File.SetUnixFileMode(
            _keyFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );
        var settingsPath = Path.Join(_basePath, "settings.json");
        WriteJson(
            settingsPath,
            new Dictionary<string, object?>
            {
                ["groqApiKey"] =
                    ApiKeyProtectionTests.EncryptLegacyGcm("legacy-secret"),
            }
        );
        var before = File.ReadAllBytes(settingsPath);

        var result = CreateService().MigrateAll();

        Assert.Equal(1, result.UnresolvedSecretCount);
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
        Assert.Equal(31, File.ReadAllBytes(_keyFilePath).Length);
    }

    [Fact]
    public void MigrateAll_UnreadableSettingsFileIsReportedInsteadOfThrowing()
    {
        var settingsPath = Path.Join(_basePath, "settings.json");
        WriteJson(
            settingsPath,
            new Dictionary<string, object?>
            {
                ["groqApiKey"] =
                    ApiKeyProtectionTests.EncryptLegacyGcm("legacy-secret"),
            }
        );
        File.SetUnixFileMode(settingsPath, UnixFileMode.None);
        if (CanRead(settingsPath))
        {
            // Running with privileges that ignore the mode (root); nothing to prove.
            return;
        }

        var result = CreateService().MigrateAll();

        Assert.Equal(0, result.MigratedFileCount);
        Assert.True(result.HasUnresolvedSecrets);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void PreservesRetiredCiphertextUntilThirdDistinctRun()
    {
        var settingsPath = Path.Join(_basePath, "settings.json");
        var ciphertext = CreateUndecryptableLegacyCiphertext("retired-secret");
        WriteJson(
            settingsPath,
            new Dictionary<string, object?> { ["groqApiKey"] = ciphertext }
        );

        var firstRun = CreateService("run-1");
        Assert.True(firstRun.MigrateAllAtStartup().HasUnresolvedSecrets);
        Assert.True(firstRun.MigrateAllAtStartup().HasUnresolvedSecrets);
        Assert.Equal(ciphertext, ReadString(settingsPath, "groqApiKey"));
        using (var afterFirstRun = ReadQuarantine())
        {
            var failure = Assert.Single(
                afterFirstRun.RootElement.GetProperty("failures").EnumerateArray()
            );
            Assert.Equal(1, failure.GetProperty("failureCount").GetInt32());
            Assert.Equal("run-1", failure.GetProperty("lastStartupId").GetString());
            Assert.Empty(
                afterFirstRun.RootElement
                    .GetProperty("retiredSecrets")
                    .EnumerateArray()
            );
        }

        Assert.True(CreateService("run-2").MigrateAllAtStartup().HasUnresolvedSecrets);

        Assert.Equal(ciphertext, ReadString(settingsPath, "groqApiKey"));
        using var afterSecondRun = ReadQuarantine();
        var secondFailure = Assert.Single(
            afterSecondRun.RootElement.GetProperty("failures").EnumerateArray()
        );
        Assert.Equal(2, secondFailure.GetProperty("failureCount").GetInt32());
        Assert.Equal("run-2", secondFailure.GetProperty("lastStartupId").GetString());
        Assert.Empty(
            afterSecondRun.RootElement.GetProperty("retiredSecrets").EnumerateArray()
        );
    }

    [Fact]
    public void ThirdFailureQuarantinesPrimaryAndBackupProviderFields()
    {
        var primaryPath = Path.Join(_basePath, "settings.json");
        var backupPath = Path.Join(_basePath, "settings.json.bak");
        var values = new Dictionary<(string Source, string Property), string>
        {
            [("settings.json", "groqApiKey")] =
                CreateUndecryptableLegacyCiphertext("primary-groq"),
            [("settings.json", "openAiApiKey")] =
                CreateUndecryptableLegacyCiphertext("primary-openai"),
            [("settings.json.bak", "groqApiKey")] =
                CreateUndecryptableLegacyCiphertext("backup-groq"),
            [("settings.json.bak", "openAiApiKey")] =
                CreateUndecryptableLegacyCiphertext("backup-openai"),
        };
        WriteJson(
            primaryPath,
            new Dictionary<string, object?>
            {
                ["groqApiKey"] = values[("settings.json", "groqApiKey")],
                ["openAiApiKey"] = values[("settings.json", "openAiApiKey")],
            }
        );
        WriteJson(
            backupPath,
            new Dictionary<string, object?>
            {
                ["groqApiKey"] = values[("settings.json.bak", "groqApiKey")],
                ["openAiApiKey"] = values[("settings.json.bak", "openAiApiKey")],
            }
        );

        CreateService("run-1").MigrateAllAtStartup();
        CreateService("run-2").MigrateAllAtStartup();
        var result = CreateService("run-3").MigrateAllAtStartup();

        Assert.Equal(2, result.MigratedFileCount);
        Assert.Equal(0, result.UnresolvedSecretCount);
        Assert.True(result.RootSettingsChanged);
        Assert.Equal("", ReadString(primaryPath, "groqApiKey"));
        Assert.Equal("", ReadString(primaryPath, "openAiApiKey"));
        Assert.Equal("", ReadString(backupPath, "groqApiKey"));
        Assert.Equal("", ReadString(backupPath, "openAiApiKey"));

        using var quarantine = ReadQuarantine();
        Assert.Equal(1, quarantine.RootElement.GetProperty("version").GetInt32());
        var retired = quarantine.RootElement
            .GetProperty("retiredSecrets")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(4, retired.Length);
        foreach (var expected in values)
        {
            var entry = Assert.Single(retired, candidate =>
                candidate.GetProperty("sourceFile").GetString() == expected.Key.Source
                && candidate.GetProperty("property").GetString() == expected.Key.Property
            );
            Assert.Equal(
                expected.Value,
                entry.GetProperty("ciphertext").GetString()
            );
            Assert.Equal(
                HashCiphertext(expected.Value),
                entry.GetProperty("ciphertextHash").GetString()
            );
            Assert.Equal(3, entry.GetProperty("failureCount").GetInt32());
            Assert.Equal("run-3", entry.GetProperty("startupId").GetString());
            Assert.NotEqual(
                default,
                entry.GetProperty("timestampUtc").GetDateTimeOffset()
            );
        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(QuarantinePath)
        );
    }

    [Fact]
    public void PersistsQuarantineBeforeCompareAndSetClear()
    {
        var settingsPath = Path.Join(_basePath, "settings.json");
        var ciphertext = CreateUndecryptableLegacyCiphertext("original-secret");
        var replacement = CreateUndecryptableLegacyCiphertext("replacement-secret");
        WriteJson(
            settingsPath,
            new Dictionary<string, object?> { ["groqApiKey"] = ciphertext }
        );
        CreateService("run-1").MigrateAllAtStartup();
        CreateService("run-2").MigrateAllAtStartup();
        var boundaryObserved = false;
        var thirdRun = CreateService(
            "run-3",
            () =>
            {
                using var durableCopy = ReadQuarantine();
                var entry = Assert.Single(
                    durableCopy.RootElement
                        .GetProperty("retiredSecrets")
                        .EnumerateArray()
                );
                Assert.Equal(ciphertext, entry.GetProperty("ciphertext").GetString());
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(QuarantinePath)
                );
                Assert.Equal(
                    ciphertext,
                    ReadString(settingsPath, "groqApiKey")
                );
                WriteJson(
                    settingsPath,
                    new Dictionary<string, object?> { ["groqApiKey"] = replacement }
                );
                boundaryObserved = true;
            }
        );

        var result = thirdRun.MigrateAllAtStartup();

        Assert.True(boundaryObserved);
        Assert.True(result.HasUnresolvedSecrets);
        Assert.Equal(replacement, ReadString(settingsPath, "groqApiKey"));
        using var quarantine = ReadQuarantine();
        Assert.Equal(
            ciphertext,
            Assert.Single(
                    quarantine.RootElement
                        .GetProperty("retiredSecrets")
                        .EnumerateArray()
                )
                .GetProperty("ciphertext")
                .GetString()
        );
    }

    [Fact]
    public void ChangedCiphertextResetsFailureCount()
    {
        var settingsPath = Path.Join(_basePath, "settings.json");
        var first = CreateUndecryptableLegacyCiphertext("first-secret");
        var replacement = CreateUndecryptableLegacyCiphertext("second-secret");
        WriteJson(
            settingsPath,
            new Dictionary<string, object?> { ["openAiApiKey"] = first }
        );
        CreateService("run-1").MigrateAllAtStartup();
        CreateService("run-2").MigrateAllAtStartup();
        WriteJson(
            settingsPath,
            new Dictionary<string, object?> { ["openAiApiKey"] = replacement }
        );

        CreateService("run-3").MigrateAllAtStartup();

        Assert.Equal(replacement, ReadString(settingsPath, "openAiApiKey"));
        using (var afterReplacement = ReadQuarantine())
        {
            var failure = Assert.Single(
                afterReplacement.RootElement
                    .GetProperty("failures")
                    .EnumerateArray()
            );
            Assert.Equal(1, failure.GetProperty("failureCount").GetInt32());
            Assert.Equal(
                HashCiphertext(replacement),
                failure.GetProperty("ciphertextHash").GetString()
            );
            Assert.Empty(
                afterReplacement.RootElement
                    .GetProperty("retiredSecrets")
                    .EnumerateArray()
            );
        }

        CreateService("run-4").MigrateAllAtStartup();
        Assert.Equal(replacement, ReadString(settingsPath, "openAiApiKey"));
        CreateService("run-5").MigrateAllAtStartup();

        Assert.Equal("", ReadString(settingsPath, "openAiApiKey"));
        using var quarantine = ReadQuarantine();
        var retired = Assert.Single(
            quarantine.RootElement.GetProperty("retiredSecrets").EnumerateArray()
        );
        Assert.Equal(replacement, retired.GetProperty("ciphertext").GetString());
    }

    [Fact]
    public void RestoredHomeDecryptsInsteadOfQuarantining()
    {
        var originalProcessHome = Environment.GetEnvironmentVariable("HOME");
        var encryptionHome = Path.Join(_basePath, "original-home");
        var wrongHome = Path.Join(_basePath, "wrong-home");
        try
        {
            Environment.SetEnvironmentVariable("HOME", encryptionHome);
            var ciphertext = ApiKeyProtectionTests.EncryptLegacyGcm("recoverable-secret");
            var settingsPath = Path.Join(_basePath, "settings.json");
            WriteJson(
                settingsPath,
                new Dictionary<string, object?> { ["groqApiKey"] = ciphertext }
            );

            Environment.SetEnvironmentVariable("HOME", wrongHome);
            CreateService("run-1").MigrateAllAtStartup();
            CreateService("run-2").MigrateAllAtStartup();
            Assert.Equal(ciphertext, ReadString(settingsPath, "groqApiKey"));

            Environment.SetEnvironmentVariable("HOME", encryptionHome);
            var result = CreateService("run-3").MigrateAllAtStartup();

            Assert.False(result.HasUnresolvedSecrets);
            AssertCurrentSecret(
                ReadString(settingsPath, "groqApiKey"),
                "recoverable-secret"
            );
            using var quarantine = ReadQuarantine();
            Assert.Empty(
                quarantine.RootElement.GetProperty("failures").EnumerateArray()
            );
            Assert.Empty(
                quarantine.RootElement
                    .GetProperty("retiredSecrets")
                    .EnumerateArray()
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalProcessHome);
        }
    }

    [Fact]
    public void DoesNotQuarantinePluginSecrets()
    {
        var pluginSettingsPath = Path.Join(
            _basePath,
            "PluginData",
            "com.test.plugin",
            "settings.json"
        );
        var ciphertext = CreateUndecryptableLegacyCiphertext("plugin-secret");
        WriteJson(
            pluginSettingsPath,
            new Dictionary<string, object?> { ["secret:api-key"] = ciphertext }
        );

        CreateService("run-1").MigrateAllAtStartup();
        CreateService("run-2").MigrateAllAtStartup();
        var result = CreateService("run-3").MigrateAllAtStartup();

        Assert.True(result.HasUnresolvedSecrets);
        Assert.Equal(
            ciphertext,
            ReadString(pluginSettingsPath, "secret:api-key")
        );
        Assert.False(File.Exists(QuarantinePath));
    }

    private static bool CanRead(string path)
    {
        try
        {
            _ = File.ReadAllText(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string QuarantinePath =>
        Path.Join(_basePath, "retired-provider-secrets.quarantine.json");

    private SecretProtectionMigrationService CreateService(
        string? startupId = null,
        Action? quarantinePersistedObserver = null
    )
    {
        return new SecretProtectionMigrationService(
            _basePath,
            _keyFilePath,
            startupId,
            quarantinePersistedObserver
        );
    }

    private void AssertCurrentSecret(string stored, string expectedPlainText)
    {
        var envelope = Convert.FromBase64String(stored);
        Assert.Equal("TWSP"u8.ToArray(), envelope[..4]);
        Assert.Equal(2, envelope[4]);
        var decrypted = ApiKeyProtection.Decrypt(stored, _keyFilePath);
        Assert.Equal(SecretProtectionFormat.Current, decrypted.Format);
        Assert.Equal(expectedPlainText, decrypted.PlainText);
    }

    private static string ReadString(string path, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(propertyName).GetString()!;
    }

    private JsonDocument ReadQuarantine()
    {
        return JsonDocument.Parse(File.ReadAllText(QuarantinePath));
    }

    private static string CreateUndecryptableLegacyCiphertext(string plainText)
    {
        var bytes = Convert.FromBase64String(
            ApiKeyProtectionTests.EncryptLegacyGcm(plainText)
        );
        bytes[^1] ^= 0x20;
        return Convert.ToBase64String(bytes);
    }

    private static string HashCiphertext(string ciphertext)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(ciphertext))
        );
    }

    private static void WriteJson(string path, Dictionary<string, object?> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(values, s_jsonOptions));
    }
}
