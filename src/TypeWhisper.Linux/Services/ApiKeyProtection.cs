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

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var key = DeriveKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
        var combined = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, combined, aes.IV.Length, encrypted.Length);
        return Convert.ToBase64String(combined);
    }

    public static string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        try
        {
            var combined = Convert.FromBase64String(encrypted);
            if (combined.Length < 16) return encrypted; // plaintext from old version
            var key = DeriveKey();
            using var aes = Aes.Create();
            aes.Key = key;
            var iv = new byte[16];
            Buffer.BlockCopy(combined, 0, iv, 0, 16);
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            var cipher = new byte[combined.Length - 16];
            Buffer.BlockCopy(combined, 16, cipher, 0, cipher.Length);
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
}
