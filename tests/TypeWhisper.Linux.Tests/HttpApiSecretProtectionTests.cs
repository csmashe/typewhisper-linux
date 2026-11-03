using System.Runtime.Versioning;
using System.Security.Cryptography;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class HttpApiSecretProtectionTests : IDisposable
{
    private readonly string _keyFilePath;
    private readonly string _tempDir;

    public HttpApiSecretProtectionTests()
    {
        _tempDir = TestPaths.CreateTempDirectory(
            "TypeWhisper.HttpApiSecretProtectionTests"
        );
        _keyFilePath = Path.Join(_tempDir, "secret-protection.key");
    }

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_tempDir);
    }

    [Fact]
    public void ProtectBearerToken_UndecryptableTokenRotatesToNewAuthenticatedV2Token()
    {
        var stored = ApiKeyProtection.Encrypt("old-token", _keyFilePath);
        var envelope = Convert.FromBase64String(stored);
        envelope[^1] ^= 0x08;
        var tampered = Convert.ToBase64String(envelope);

        var result = HttpApiService.ProtectBearerToken(tampered, _keyFilePath);
        var decrypted = ApiKeyProtection.Decrypt(
            result.StoredValue,
            _keyFilePath
        );

        Assert.True(result.Changed);
        Assert.NotEqual(tampered, result.StoredValue);
        Assert.NotEqual("old-token", result.PlainText);
        Assert.Equal(64, result.PlainText.Length);
        Assert.Equal(SecretProtectionFormat.Current, decrypted.Format);
        Assert.Equal(result.PlainText, decrypted.PlainText);
    }

    [Fact]
    public void ProtectBearerToken_LegacyGcmKeepsTokenValueAndMigratesEnvelope()
    {
        var legacy = ApiKeyProtectionTests.EncryptLegacyGcm("legacy-token");

        var result = HttpApiService.ProtectBearerToken(legacy, _keyFilePath);
        var envelope = Convert.FromBase64String(result.StoredValue);

        Assert.True(result.Changed);
        Assert.Equal("legacy-token", result.PlainText);
        Assert.Equal("TWSP"u8.ToArray(), envelope[..4]);
        Assert.Equal(2, envelope[4]);
        Assert.Equal(
            "legacy-token",
            HttpApiService.ReadBearerToken(
                new AppSettings { ApiServerBearerToken = result.StoredValue },
                _keyFilePath
            )
        );
    }

    [Fact]
    public void ReadBearerToken_UndecryptableValueNeverReturnsCiphertext()
    {
        var foreignDirectory = TestPaths.CreateTempDirectory(
            "TypeWhisper.HttpApiForeignKey"
        );
        try
        {
            var foreign = ApiKeyProtection.Encrypt(
                "foreign-token",
                Path.Join(foreignDirectory, "foreign.key")
            );
            ApiKeyProtection.Encrypt("local-seed", _keyFilePath);

            var token = HttpApiService.ReadBearerToken(
                new AppSettings { ApiServerBearerToken = foreign },
                _keyFilePath
            );

            Assert.Equal("", token);
            Assert.NotEqual(foreign, token);
        }
        finally
        {
            TestPaths.DeleteDirectory(foreignDirectory);
        }
    }

    [Fact]
    public void ProtectBearerToken_LegacyPlaintextHexTokenIsReprotectedNotRotated()
    {
        // Shape written by pre-encryption builds: Convert.ToHexString of 32 random bytes.
        var legacy = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var result = HttpApiService.ProtectBearerToken(legacy, _keyFilePath);
        var envelope = Convert.FromBase64String(result.StoredValue);
        var decrypted = ApiKeyProtection.Decrypt(result.StoredValue, _keyFilePath);

        Assert.True(result.Changed);
        Assert.Equal(legacy, result.PlainText);
        Assert.Equal(legacy, decrypted.PlainText);
        Assert.Equal(SecretProtectionFormat.Current, decrypted.Format);
        Assert.Equal("TWSP"u8.ToArray(), envelope[..4]);
        Assert.Equal(2, envelope[4]);
    }

    [Fact]
    public void ProtectBearerToken_CurrentTokenIsLeftUnchanged()
    {
        var stored = ApiKeyProtection.Encrypt("current-token", _keyFilePath);

        var result = HttpApiService.ProtectBearerToken(stored, _keyFilePath);

        Assert.False(result.Changed);
        Assert.Equal(stored, result.StoredValue);
        Assert.Equal("current-token", result.PlainText);
    }
}
