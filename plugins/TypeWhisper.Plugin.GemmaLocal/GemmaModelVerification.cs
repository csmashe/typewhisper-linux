using System.Globalization;
using System.Security.Cryptography;

namespace TypeWhisper.Plugin.GemmaLocal;

/// <summary>A published GGUF build: exact byte size and SHA-256.</summary>
internal readonly record struct GemmaModelFile(long SizeBytes, string Sha256);

/// <summary>
///     Integrity checks for cached Gemma models. Downloads must match the pinned build; a
///     cached file may also match an earlier build with the same weights (only embedded
///     metadata such as the unused chat template differs). Re-quantizations are not accepted.
/// </summary>
internal static class GemmaModelVerification
{
    private const int HashBufferSize = 1024 * 1024;

    internal static string StampPath(string modelPath) => modelPath + ".verified";

    internal static bool HasAcceptedSize(GemmaModelDefinition model, string path)
    {
        var file = Describe(path);
        return file.Exists && model.AcceptedFiles.Any(f => f.SizeBytes == file.Length);
    }

    /// <summary>
    ///     True when the stamp written by the last verification still describes the file
    ///     (same size and modification time) and names an accepted build. Avoids re-hashing
    ///     a multi-gigabyte file on every load.
    /// </summary>
    internal static bool IsVerified(GemmaModelDefinition model, string path)
    {
        try
        {
            var file = Describe(path);
            var stampPath = StampPath(path);
            if (!file.Exists || !File.Exists(stampPath))
                return false;

            var parts = File.ReadAllText(stampPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && size == file.Length
                && ticks == file.LastWriteTimeUtc.Ticks
                && model.AcceptedFiles.Contains(new GemmaModelFile(size, parts[0]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Hashes a cached file against every accepted build and stamps it on success.</summary>
    internal static async Task VerifyCachedAsync(
        GemmaModelDefinition model,
        string path,
        CancellationToken ct)
    {
        TryDelete(StampPath(path));
        var before = Describe(path);
        if (!before.Exists)
            throw new FileNotFoundException($"Model file not found: {path}");

        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        if (model.AcceptedFiles.All(f => f.SizeBytes != length))
            throw new InvalidDataException(MismatchMessage(model));

        var sha256 = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        if (!model.AcceptedFiles.Contains(new GemmaModelFile(length, sha256)))
            throw new InvalidDataException(MismatchMessage(model));

        var after = Describe(path);
        if (after.Length != length || after.LastWriteTimeUtc != modified)
            throw new IOException($"The {model.DisplayName} model changed during verification. Try again.");

        WriteStamp(path, sha256, after);
    }

    /// <summary>
    ///     Download completion check: only the pinned build is accepted. Synchronous because the
    ///     downloader's verifier is; a cancelled hash would discard a complete download.
    /// </summary>
    internal static string VerifyDownloaded(GemmaModelDefinition model, string partialPath)
    {
        var length = new FileInfo(partialPath).Length;
        if (length != model.Pinned.SizeBytes)
            throw new InvalidDataException(
                $"The downloaded {model.DisplayName} model has {length} bytes; expected "
                    + $"{model.Pinned.SizeBytes}. Please retry the download.");

        string sha256;
        using (var stream = OpenForHashing(partialPath))
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));

        if (!string.Equals(sha256, model.Pinned.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The downloaded {model.DisplayName} model failed its integrity check "
                    + $"(expected SHA-256 {model.Pinned.Sha256}, got {sha256}). Please retry the download.");

        return sha256;
    }

    /// <summary>The file itself, or a symlink's final target: a link's own size and time say nothing about the model.</summary>
    internal static FileInfo Describe(string path)
    {
        var file = new FileInfo(path);
        return file.LinkTarget is null ? file : file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? file;
    }

    internal static void WriteStamp(string path, string sha256, FileInfo file)
    {
        var stampPath = StampPath(path);
        // Per-call temp: load and download can verify the same file concurrently.
        var temp = stampPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temp,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{sha256} {file.Length} {file.LastWriteTimeUtc.Ticks}"));
            File.Move(temp, stampPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale stamp only costs a re-hash; it never vouches for a changed file.
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = OpenForHashing(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static FileStream OpenForHashing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

    private static string MismatchMessage(GemmaModelDefinition model) =>
        $"The downloaded {model.DisplayName} model failed its integrity check. Select the model again to re-download it.";
}
