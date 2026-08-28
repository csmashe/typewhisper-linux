using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
internal static partial class ApiKeyProtection
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
        return EnsureKeyFileCore(keyFilePath, openedFileObserver: null);
    }

    /// <summary>
    ///     Test-only entry point whose observer runs after a key descriptor is opened, but
    ///     before that descriptor is validated and read. On a create-then-read run the
    ///     observer fires once per hardened open, so it can fire more than once.
    /// </summary>
    internal static byte[] EnsureKeyFileForTests(
        string keyFilePath,
        Action openedFileObserver
    )
    {
        return EnsureKeyFileCore(keyFilePath, openedFileObserver);
    }

    private static byte[] EnsureKeyFileCore(
        string? keyFilePath,
        Action? openedFileObserver
    )
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

        var existingKey = TryReadExistingKeyFile(path, openedFileObserver);
        if (existingKey is not null)
        {
            return existingKey;
        }

        var generated = RandomNumberGenerator.GetBytes(KeySize);
        // ReSharper disable once TryStatementsCanBeMerged -- the outer try/finally exists solely to
        // guarantee the generated key material is zeroed; keeping it separate from the
        // concurrent-creator catch keeps that guarantee obvious in this security-critical path.
        try
        {
            try
            {
                // Publishing the complete staged 0600 file preserves atomic visibility; opening
                // the final path with O_CREAT | O_EXCL would expose partial key material.
                AtomicFileWrite.WriteAllBytesCreateNew(path, generated, KeyFileMode);
            }
            catch (IOException ex)
                when (ex is not AtomicFileWriteIndeterminateCommitException)
            {
                try
                {
                    existingKey = TryReadExistingKeyFile(path, openedFileObserver);
                }
                catch (SecretProtectionException inner)
                {
                    // Keep the write failure visible: the re-read's rejection alone
                    // would hide e.g. an ENOSPC behind a not-a-regular-file message.
                    throw new SecretProtectionException(
                        inner.Message,
                        new AggregateException(ex, inner)
                    );
                }

                if (existingKey is not null)
                {
                    return existingKey;
                }

                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
        }

        return TryReadExistingKeyFile(path, openedFileObserver)
            ?? throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated."
            );
    }

    private static byte[]? TryReadExistingKeyFile(
        string path,
        Action? openedFileObserver
    )
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SecretProtectionException(
                "Secret protection requires verifiable Unix file permissions."
            );
        }

        var fd = NativeFile.OpenExisting(path, out var openError);
        if (fd < 0)
        {
            if (openError == NativeFile.ErrorNoEntry)
            {
                return null;
            }

            if (openError == NativeFile.ErrorSymbolicLink)
            {
                throw new SecretProtectionException(
                    $"Secret protection key '{path}' must be a regular file, not a symbolic link."
                );
            }

            throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated.",
                new Win32Exception(openError)
            );
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        openedFileObserver?.Invoke();

        if (!NativeFile.TryGetStatus(fd, out var stat, out var statError))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated.",
                new Win32Exception(statError)
            );
        }

        if (!NativeFile.HasTypeAndMode(stat))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' file type, permissions, and ownership could not be determined."
            );
        }

        if (!NativeFile.IsRegular(stat.Mode))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' must be a regular file."
            );
        }

        if (!NativeFile.HasOwnerOnlyMode(stat.Mode))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' must have Unix mode 0600."
            );
        }

        // Mode 0600 does not prove the file is ours: under sudo with a shared
        // HOME another user could plant a key they know. Reject foreign owners.
        if (!NativeFile.IsOwnedByEffectiveUser(stat))
        {
            throw new SecretProtectionException(
                $"Secret protection key '{path}' must be owned by the current user."
            );
        }

        var key = new byte[KeySize];
        try
        {
            var bytesRead = 0;
            while (bytesRead < key.Length)
            {
                var read = RandomAccess.Read(
                    handle,
                    key.AsSpan(bytesRead),
                    bytesRead
                );
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            Span<byte> extraByte = stackalloc byte[1];
            if (
                bytesRead != KeySize
                || RandomAccess.Read(handle, extraByte, KeySize) != 0
            )
            {
                extraByte.Clear();
                throw new SecretProtectionException(
                    $"Secret protection key '{path}' must contain exactly {KeySize} bytes."
                );
            }

            return key;
        }
        catch (SecretProtectionException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new SecretProtectionException(
                $"Secret protection key '{path}' could not be validated.",
                ex
            );
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
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

        byte[] key;
        try
        {
            var path = keyFilePath ?? TypeWhisperEnvironment.SecretProtectionKeyFilePath;
            var existingKey = TryReadExistingKeyFile(path, openedFileObserver: null);
            if (existingKey is null)
            {
                return SecretDecryptionResult.Failure;
            }

            key = existingKey;
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

    private static partial class NativeFile
    {
        public const int ErrorNoEntry = 2;
        public const int ErrorSymbolicLink = 40;

        private const int ErrorInterrupted = 4;
        private const int AtEmptyPath = 0x1000;
        private const uint StatxType = 0x0001;
        private const uint StatxMode = 0x0002;
        private const uint StatxUid = 0x0008;
        private const uint StatxTypeAndMode = StatxType | StatxMode | StatxUid;
        private const int OpenReadOnly = 0;
        private const int OpenCloseOnExec = 0x80000;
        private const int OpenNoFollow = 0x20000;
        private const int OpenNonBlock = 0x800;
        private const ushort FileTypeMask = 0xF000;
        private const ushort FileTypeRegular = 0x8000;
        private const ushort PermissionAndSpecialBitsMask = 0x0FFF;
        private const ushort OwnerReadWriteMode = 0x0180;

        public static int OpenExisting(string path, out int error)
        {
            while (true)
            {
                // O_NONBLOCK prevents a hostile FIFO from hanging before descriptor validation.
                var fd = open(
                    path,
                    OpenReadOnly | OpenCloseOnExec | OpenNoFollow | OpenNonBlock
                );
                if (fd >= 0)
                {
                    error = 0;
                    return fd;
                }

                error = Marshal.GetLastPInvokeError();
                if (error != ErrorInterrupted)
                {
                    return -1;
                }
            }
        }

        public static bool TryGetStatus(
            int fd,
            out StatxBuffer stat,
            out int error
        )
        {
            while (true)
            {
                if (
                    statx(
                        fd,
                        string.Empty,
                        AtEmptyPath,
                        StatxTypeAndMode,
                        out stat
                    ) == 0
                )
                {
                    error = 0;
                    return true;
                }

                error = Marshal.GetLastPInvokeError();
                if (error != ErrorInterrupted)
                {
                    stat = default;
                    return false;
                }
            }
        }

        public static bool HasTypeAndMode(StatxBuffer stat)
        {
            return (stat.Mask & StatxTypeAndMode) == StatxTypeAndMode;
        }

        public static bool IsRegular(ushort mode)
        {
            return (mode & FileTypeMask) == FileTypeRegular;
        }

        public static bool IsOwnedByEffectiveUser(StatxBuffer stat)
        {
            return stat.UserId == geteuid();
        }

        public static bool HasOwnerOnlyMode(ushort mode)
        {
            return (mode & PermissionAndSpecialBitsMask) == OwnerReadWriteMode;
        }

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        public struct StatxBuffer
        {
            public uint Mask;
            public uint BlockSize;
            public ulong Attributes;
            public uint LinkCount;
            public uint UserId;
            public uint GroupId;
            public ushort Mode;
        }

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int open(string path, int flags);

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc")]
        private static partial uint geteuid();

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int statx(
            int directoryFileDescriptor,
            string path,
            int flags,
            uint mask,
            out StatxBuffer buffer
        );
    }
}
