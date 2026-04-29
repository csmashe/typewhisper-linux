using System.Security.Cryptography;
using System.Text;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Linux at-rest protection for plugin secrets. v1 uses a per-user key
/// derived from the user's UID and $XDG_DATA_HOME to provide obfuscation
/// equivalent to DPAPI's CurrentUser scope (not strong cryptography — an
/// attacker with file access can decrypt, same as DPAPI).
///
/// TODO: swap to Tmds.LibSecret against the Secret Service (GNOME Keyring /
/// KWallet) once the UI surfaces a "store keys in keyring" preference.
/// Falling back to file-at-rest when no keyring daemon is running.
/// </summary>
public static class ApiKeyProtection
{
    private static readonly byte[] Entropy = "TypeWhisper.ApiKey.v1.linux"u8.ToArray();
    private const byte AesGcmVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int LegacyIvSize = 16;

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var key = DeriveKey();
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[bytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, bytes, cipher, tag);
        var combined = new byte[1 + NonceSize + TagSize + cipher.Length];
        combined[0] = AesGcmVersion;
        Buffer.BlockCopy(nonce, 0, combined, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, combined, 1 + NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        try
        {
            var combined = Convert.FromBase64String(encrypted);
            if (TryDecryptAesGcm(combined, out var decryptedText))
                return decryptedText;

            if (combined.Length < LegacyIvSize) return encrypted; // plaintext from old version
            var key = DeriveKey();
            using var aes = Aes.Create();
            aes.Key = key;
            var iv = new byte[LegacyIvSize];
            Buffer.BlockCopy(combined, 0, iv, 0, LegacyIvSize);
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            var cipher = new byte[combined.Length - LegacyIvSize];
            Buffer.BlockCopy(combined, LegacyIvSize, cipher, 0, cipher.Length);
            var decrypted = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            return encrypted;
        }
        catch (FormatException)
        {
            return encrypted;
        }
    }

    private static byte[] DeriveKey()
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/";
        var user = Environment.UserName;
        var material = Encoding.UTF8.GetBytes($"{user}:{home}");
        return Rfc2898DeriveBytes.Pbkdf2(material, Entropy, 10_000, HashAlgorithmName.SHA256, 32);
    }

    private static bool TryDecryptAesGcm(byte[] combined, out string decryptedText)
    {
        decryptedText = "";
        if (combined.Length < 1 + NonceSize + TagSize || combined[0] != AesGcmVersion)
            return false;

        var nonce = combined.AsSpan(1, NonceSize);
        var tag = combined.AsSpan(1 + NonceSize, TagSize);
        var cipher = combined.AsSpan(1 + NonceSize + TagSize);
        var plaintext = new byte[cipher.Length];
        var key = DeriveKey();
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plaintext);
        decryptedText = Encoding.UTF8.GetString(plaintext);
        return true;
    }
}
