using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Core;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

internal enum SecretProtectionFormat
{
    Current,
    LegacyGcm,
    LegacyCbc,
    LegacyPlaintext,
    Failure,
}

internal readonly record struct SecretDecryptionResult(
    SecretProtectionFormat Format,
    string? PlainText
)
{
    public bool Succeeded => Format != SecretProtectionFormat.Failure;
    public bool RequiresMigration =>
        Format is SecretProtectionFormat.LegacyGcm
            or SecretProtectionFormat.LegacyCbc
            or SecretProtectionFormat.LegacyPlaintext;

    public static SecretDecryptionResult Failure =>
        new(SecretProtectionFormat.Failure, null);
}

internal sealed class SecretProtectionException(string message, Exception? innerException = null)
    : CryptographicException(message, innerException);

/// <summary>
///     Authenticated at-rest protection for application and plugin secrets.
/// </summary>
internal static class ApiKeyProtection
{
    private const byte LegacyAesGcmVersion = 1;
    private const byte CurrentVersion = 2;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int LegacyIvSize = 16;
    private const int CurrentHeaderSize = 5;

    private const UnixFileMode KeyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly byte[] s_magic = "TWSP"u8.ToArray();
    private static readonly byte[] s_legacyEntropy =
        "TypeWhisper.ApiKey.v1.linux"u8.ToArray();
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    public static string Encrypt(string plainText, string? keyFilePath = null)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return "";
        }

        var key = EnsureKeyFile(keyFilePath);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[bytes.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, bytes, cipher, tag);

            var combined = new byte[
                CurrentHeaderSize + NonceSize + TagSize + cipher.Length
            ];
            s_magic.CopyTo(combined, 0);
            combined[s_magic.Length] = CurrentVersion;
            Buffer.BlockCopy(nonce, 0, combined, CurrentHeaderSize, NonceSize);
            Buffer.BlockCopy(
                tag,
                0,
                combined,
                CurrentHeaderSize + NonceSize,
                TagSize
            );
            Buffer.BlockCopy(
                cipher,
                0,
                combined,
                CurrentHeaderSize + NonceSize + TagSize,
                cipher.Length
            );
            return Convert.ToBase64String(combined);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static SecretDecryptionResult Decrypt(
        string encrypted,
        string? keyFilePath = null
    )
    {
        if (string.IsNullOrEmpty(encrypted))
        {
            return new SecretDecryptionResult(
                SecretProtectionFormat.LegacyPlaintext,
                ""
            );
        }

        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(encrypted);
        }
        catch (FormatException)
        {
            return new SecretDecryptionResult(
                SecretProtectionFormat.LegacyPlaintext,
                encrypted
            );
        }

        if (HasCurrentMagic(combined))
        {
            return TryDecryptCurrent(combined, keyFilePath);
        }

        if (
            combined.Length >= 1 + NonceSize + TagSize
            && combined[0] == LegacyAesGcmVersion
        )
        {
            var legacyGcm = TryDecryptLegacyGcm(combined);
            if (legacyGcm.Succeeded)
            {
                return legacyGcm;
            }
        }

        if (IsLegacyCbcShape(combined))
        {
            return TryDecryptLegacyCbc(combined);
        }

        if (combined.Length < LegacyIvSize)
        {
            return new SecretDecryptionResult(
                SecretProtectionFormat.LegacyPlaintext,
                encrypted
            );
        }

        return SecretDecryptionResult.Failure;
    }

    public static byte[] EnsureKeyFile(string? keyFilePath = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SecretProtectionException(
                "Secret protection requires verifiable Unix file permissions."
            );
        }

        var path = keyFilePath ?? TypeWhisperEnvironment.SecretProtectionKeyFilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(path))
        {
            var generated = RandomNumberGenerator.GetBytes(KeySize);
            // ReSharper disable once TryStatementsCanBeMerged -- the outer try/finally exists solely to
            // guarantee the generated key material is zeroed; keeping it separate from the
            // concurrent-creator catch keeps that guarantee obvious in this security-critical path.
            try
            {
                try
                {
                    AtomicFileWrite.WriteAllBytesCreateNew(path, generated, KeyFileMode);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // A concurrent creator won. Its file is validated below.
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
            }
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            if (mode != KeyFileMode)
            {
                throw new SecretProtectionException(
                    $"Secret protection key '{path}' must have Unix mode 0600."
                );
            }

            var key = File.ReadAllBytes(path);
            // ReSharper disable once InvertIf -- reject-then-return matches the guard style used by the
            // mode check above; inverting would hide the zero-and-throw rejection behind the happy path.
            if (key.Length != KeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new SecretProtectionException(
                    $"Secret protection key '{path}' must contain exactly {KeySize} bytes."
                );
            }

            return key;
        }
        catch (SecretProtectionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated.",
                ex
            );
        }
    }

    private static SecretDecryptionResult TryDecryptCurrent(
        byte[] combined,
        string? keyFilePath
    )
    {
        if (
            combined.Length < CurrentHeaderSize + NonceSize + TagSize
            || combined[s_magic.Length] != CurrentVersion
        )
        {
            return SecretDecryptionResult.Failure;
        }

        var path = keyFilePath ?? TypeWhisperEnvironment.SecretProtectionKeyFilePath;
        if (!File.Exists(path))
        {
            return SecretDecryptionResult.Failure;
        }

        byte[] key;
        try
        {
            key = EnsureKeyFile(path);
        }
        catch (SecretProtectionException)
        {
            return SecretDecryptionResult.Failure;
        }

        try
        {
            var nonce = combined.AsSpan(CurrentHeaderSize, NonceSize);
            var tag = combined.AsSpan(CurrentHeaderSize + NonceSize, TagSize);
            var cipher = combined.AsSpan(
                CurrentHeaderSize + NonceSize + TagSize
            );
            var plaintext = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return new SecretDecryptionResult(
                SecretProtectionFormat.Current,
                s_strictUtf8.GetString(plaintext)
            );
        }
        catch (Exception ex) when (
            ex is CryptographicException or DecoderFallbackException
        )
        {
            return SecretDecryptionResult.Failure;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static SecretDecryptionResult TryDecryptLegacyGcm(byte[] combined)
    {
        var key = DeriveLegacyKey();
        try
        {
            var nonce = combined.AsSpan(1, NonceSize);
            var tag = combined.AsSpan(1 + NonceSize, TagSize);
            var cipher = combined.AsSpan(1 + NonceSize + TagSize);
            var plaintext = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return new SecretDecryptionResult(
                SecretProtectionFormat.LegacyGcm,
                s_strictUtf8.GetString(plaintext)
            );
        }
        catch (Exception ex) when (
            ex is CryptographicException or DecoderFallbackException
        )
        {
            return SecretDecryptionResult.Failure;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static SecretDecryptionResult TryDecryptLegacyCbc(byte[] combined)
    {
        var key = DeriveLegacyKey();
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = combined.AsSpan(0, LegacyIvSize).ToArray();
            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(
                combined,
                LegacyIvSize,
                combined.Length - LegacyIvSize
            );
            return new SecretDecryptionResult(
                SecretProtectionFormat.LegacyCbc,
                s_strictUtf8.GetString(decrypted)
            );
        }
        catch (Exception ex) when (
            ex is CryptographicException or DecoderFallbackException
        )
        {
            return SecretDecryptionResult.Failure;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveLegacyKey()
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/";
        var user = Environment.UserName;
        var material = Encoding.UTF8.GetBytes($"{user}:{home}");
        return Rfc2898DeriveBytes.Pbkdf2(
            material,
            s_legacyEntropy,
            10_000,
            HashAlgorithmName.SHA256,
            KeySize
        );
    }

    private static bool HasCurrentMagic(byte[] combined)
    {
        return combined.Length >= CurrentHeaderSize
            && combined.AsSpan(0, s_magic.Length).SequenceEqual(s_magic);
    }

    private static bool IsLegacyCbcShape(byte[] combined)
    {
        return combined.Length >= LegacyIvSize * 2
            && (combined.Length - LegacyIvSize) % LegacyIvSize == 0;
    }
}
