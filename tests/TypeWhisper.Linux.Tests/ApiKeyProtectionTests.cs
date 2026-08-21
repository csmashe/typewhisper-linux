using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class ApiKeyProtectionTests : IDisposable
{
    private readonly string _keyFilePath;
    private readonly string _tempDir;

    public ApiKeyProtectionTests()
    {
        _tempDir = TestPaths.CreateTempDirectory(
            "TypeWhisper.ApiKeyProtectionTests"
        );
        _keyFilePath = Path.Join(_tempDir, "secret-protection.key");
    }

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_tempDir);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsWithV2EnvelopeAndOwnerOnlyRandomKey()
    {
        const string secret = "super-secret-token";

        var encrypted = ApiKeyProtection.Encrypt(secret, _keyFilePath);
        var decrypted = ApiKeyProtection.Decrypt(encrypted, _keyFilePath);
        var envelope = Convert.FromBase64String(encrypted);

        Assert.Equal("TWSP"u8.ToArray(), envelope[..4]);
        Assert.Equal(2, envelope[4]);
        Assert.Equal(SecretProtectionFormat.Current, decrypted.Format);
        Assert.Equal(secret, decrypted.PlainText);
        Assert.Equal(32, File.ReadAllBytes(_keyFilePath).Length);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_keyFilePath)
        );
    }

    [Fact]
    public void Decrypt_TamperedV2Ciphertext_ReturnsExplicitFailure()
    {
        var encrypted = ApiKeyProtection.Encrypt(
            "super-secret-token",
            _keyFilePath
        );
        var envelope = Convert.FromBase64String(encrypted);
        envelope[^1] ^= 0x40;

        var decrypted = ApiKeyProtection.Decrypt(
            Convert.ToBase64String(envelope),
            _keyFilePath
        );

        Assert.Equal(SecretProtectionFormat.Failure, decrypted.Format);
        Assert.Null(decrypted.PlainText);
    }

    [Fact]
    public void Decrypt_V2CiphertextWithDifferentKey_ReturnsFailure()
    {
        var encrypted = ApiKeyProtection.Encrypt("secret", _keyFilePath);
        var otherKeyPath = Path.Join(_tempDir, "other.key");
        ApiKeyProtection.Encrypt("seed", otherKeyPath);

        var decrypted = ApiKeyProtection.Decrypt(encrypted, otherKeyPath);

        Assert.Equal(SecretProtectionFormat.Failure, decrypted.Format);
        Assert.Null(decrypted.PlainText);
    }

    [Fact]
    public void Decrypt_LegacyGcm_ReturnsAuthenticatedLegacyMetadata()
    {
        var encrypted = EncryptLegacyGcm("legacy-secret");

        var decrypted = ApiKeyProtection.Decrypt(encrypted, _keyFilePath);

        Assert.Equal(SecretProtectionFormat.LegacyGcm, decrypted.Format);
        Assert.Equal("legacy-secret", decrypted.PlainText);
        Assert.False(File.Exists(_keyFilePath));
    }

    [Fact]
    public void Decrypt_CbcWhoseIvStartsWithV1Byte_FallsBackAfterGcmFailure()
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        iv[0] = 1;
        var encrypted = EncryptLegacyCbc("cbc-secret"u8.ToArray(), iv);

        var decrypted = ApiKeyProtection.Decrypt(encrypted, _keyFilePath);

        Assert.Equal(SecretProtectionFormat.LegacyCbc, decrypted.Format);
        Assert.Equal("cbc-secret", decrypted.PlainText);
    }

    [Fact]
    public void Decrypt_LegacyCbcWithInvalidUtf8_ReturnsFailure()
    {
        var encrypted = EncryptLegacyCbc([0xff, 0xfe, 0xfd]);

        var decrypted = ApiKeyProtection.Decrypt(encrypted, _keyFilePath);

        Assert.Equal(SecretProtectionFormat.Failure, decrypted.Format);
        Assert.Null(decrypted.PlainText);
    }

    [Theory]
    [InlineData("plain-text-secret")]
    [InlineData("AQID")]
    public void Decrypt_ClearLegacyPlaintext_ReturnsPlaintextMetadata(string stored)
    {
        var decrypted = ApiKeyProtection.Decrypt(stored, _keyFilePath);

        Assert.Equal(SecretProtectionFormat.LegacyPlaintext, decrypted.Format);
        Assert.Equal(stored, decrypted.PlainText);
    }

    [Fact]
    public void Decrypt_Base64AtLeastIvLengthButNotRecognized_ReturnsFailure()
    {
        var stored = Convert.ToBase64String(RandomNumberGenerator.GetBytes(17));

        var decrypted = ApiKeyProtection.Decrypt(stored, _keyFilePath);

        Assert.Equal(SecretProtectionFormat.Failure, decrypted.Format);
        Assert.Null(decrypted.PlainText);
    }

    [Fact]
    public void Encrypt_MalformedExistingKey_DoesNotRegenerateIt()
    {
        var malformed = RandomNumberGenerator.GetBytes(31);
        File.WriteAllBytes(_keyFilePath, malformed);
        File.SetUnixFileMode(
            _keyFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );

        Assert.Throws<SecretProtectionException>(() =>
            ApiKeyProtection.Encrypt("secret", _keyFilePath)
        );
        Assert.Equal(malformed, File.ReadAllBytes(_keyFilePath));
    }

    [Fact]
    public void EnsureKeyFile_OverlengthKey_FailsClosed()
    {
        var overlength = RandomNumberGenerator.GetBytes(33);
        File.WriteAllBytes(_keyFilePath, overlength);
        File.SetUnixFileMode(
            _keyFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );

        var exception = Assert.Throws<SecretProtectionException>(() =>
            ApiKeyProtection.EnsureKeyFile(_keyFilePath)
        );

        Assert.Contains("exactly", exception.Message);
        Assert.Equal(overlength, File.ReadAllBytes(_keyFilePath));
    }

    [Fact]
    public void EnsureKeyFile_SymlinkToValidKey_FailsClosedWithoutChangingEither()
    {
        var targetPath = Path.Join(_tempDir, "target.key");
        var targetBytes = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(targetPath, targetBytes);
        File.SetUnixFileMode(
            targetPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );
        File.CreateSymbolicLink(_keyFilePath, targetPath);

        Assert.Throws<SecretProtectionException>(() =>
            ApiKeyProtection.EnsureKeyFile(_keyFilePath)
        );
        Assert.Equal(targetBytes, File.ReadAllBytes(targetPath));
        Assert.Equal(targetPath, new FileInfo(_keyFilePath).LinkTarget);
    }

    [Fact]
    public void EnsureKeyFile_KeyWithoutExact0600Mode_FailsClosed()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_keyFilePath, keyBytes);
        File.SetUnixFileMode(
            _keyFilePath,
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead
        );

        var exception = Assert.Throws<SecretProtectionException>(() =>
            ApiKeyProtection.EnsureKeyFile(_keyFilePath)
        );
        Assert.Contains("0600", exception.Message);
        Assert.Equal(keyBytes, File.ReadAllBytes(_keyFilePath));
    }

    [Fact]
    public void EnsureKeyFile_DirectoryWith0600Mode_FailsRegularFileValidation()
    {
        Directory.CreateDirectory(_keyFilePath);
        try
        {
            File.SetUnixFileMode(
                _keyFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
            var exception = Assert.Throws<SecretProtectionException>(() =>
                ApiKeyProtection.EnsureKeyFile(_keyFilePath)
            );
            Assert.Contains("regular file", exception.Message);
        }
        finally
        {
            File.SetUnixFileMode(
                _keyFilePath,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
            );
        }
    }

    [Fact]
    public void EnsureKeyFile_PathReplacedAfterOpen_ReadsValidatedDescriptor()
    {
        var originalPath = Path.Join(_tempDir, "opened-original.key");
        var originalBytes = RandomNumberGenerator.GetBytes(32);
        var replacementBytes = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_keyFilePath, originalBytes);
        File.SetUnixFileMode(
            _keyFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );

        var loaded = ApiKeyProtection.EnsureKeyFileForTests(
            _keyFilePath,
            () =>
            {
                File.Move(_keyFilePath, originalPath);
                File.WriteAllBytes(_keyFilePath, replacementBytes);
                File.SetUnixFileMode(
                    _keyFilePath,
                    UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead
                );
            }
        );

        Assert.Equal(originalBytes, loaded);
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPath));
        Assert.Equal(replacementBytes, File.ReadAllBytes(_keyFilePath));
        Assert.Equal(
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead,
            File.GetUnixFileMode(_keyFilePath)
        );
    }

    [Fact]
    public void Decrypt_CurrentEnvelopeWithDeletedKey_ReturnsFailureWithoutRecreatingKey()
    {
        var encrypted = ApiKeyProtection.Encrypt("secret", _keyFilePath);
        File.Delete(_keyFilePath);

        var decrypted = ApiKeyProtection.Decrypt(encrypted, _keyFilePath);

        Assert.Equal(SecretProtectionFormat.Failure, decrypted.Format);
        Assert.Null(decrypted.PlainText);
        Assert.False(File.Exists(_keyFilePath));
    }

    internal static string EncryptLegacyGcm(string plainText)
    {
        var key = DeriveLegacyKey();
        var plaintext = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        var combined = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        combined[0] = 1;
        Buffer.BlockCopy(nonce, 0, combined, 1, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, 1 + nonce.Length, tag.Length);
        Buffer.BlockCopy(
            cipher,
            0,
            combined,
            1 + nonce.Length + tag.Length,
            cipher.Length
        );
        return Convert.ToBase64String(combined);
    }

    private static string EncryptLegacyCbc(byte[] plaintext, byte[]? iv = null)
    {
        var key = DeriveLegacyKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv ?? RandomNumberGenerator.GetBytes(16);
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        var combined = new byte[16 + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, 16);
        Buffer.BlockCopy(cipher, 0, combined, 16, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    private static byte[] DeriveLegacyKey()
    {
        var material = Encoding.UTF8.GetBytes(
            $"{Environment.UserName}:{Environment.GetEnvironmentVariable("HOME") ?? "/"}"
        );
        return Rfc2898DeriveBytes.Pbkdf2(
            material,
            "TypeWhisper.ApiKey.v1.linux"u8.ToArray(),
            10_000,
            HashAlgorithmName.SHA256,
            32
        );
    }
}
