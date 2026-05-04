using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ApiKeyProtectionTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrips()
    {
        const string secret = "super-secret-token";

        var encrypted = ApiKeyProtection.Encrypt(secret);
        var decrypted = ApiKeyProtection.Decrypt(encrypted);

        Assert.NotEqual(secret, encrypted);
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_DoesNotReturnPlaintext()
    {
        var encrypted = ApiKeyProtection.Encrypt("super-secret-token");
        var tampered = encrypted[..^1] + (encrypted[^1] == 'A' ? 'B' : 'A');

        var decrypted = ApiKeyProtection.Decrypt(tampered);

        Assert.NotEqual("super-secret-token", decrypted);
    }
}
