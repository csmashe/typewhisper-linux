using System.Security.Cryptography;
using System.Runtime.Versioning;
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

    private SecretProtectionMigrationService CreateService()
    {
        return new SecretProtectionMigrationService(_basePath, _keyFilePath);
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

    private static void WriteJson(string path, Dictionary<string, object?> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(values, s_jsonOptions));
    }
}
