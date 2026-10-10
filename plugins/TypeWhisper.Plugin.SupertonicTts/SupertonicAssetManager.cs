using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.SupertonicTts;

/// <summary>A pinned model file: where it is stored, where it comes from, and its exact size and SHA-256.</summary>
internal sealed record SupertonicAssetFile(string RelativePath, string RemotePath, long SizeBytes, string Sha256);

/// <summary>
///     Downloads the Supertonic 3 assets from one pinned repository revision and treats a
///     file as ready only when its size and SHA-256 match. A successful check is stamped
///     beside the file (<c>.verified</c>) so later sessions do not re-hash the model.
/// </summary>
internal sealed class SupertonicAssetManager : ISupertonicAssetManager, IDisposable
{
    internal const string Revision = "3cadd1ee6394adea1bd021217a0e650ede09a323";
    private const string ModelSourceUrl = "https://huggingface.co/Supertone/supertonic-3";
    private const string DefaultBaseUrl = $"{ModelSourceUrl}/resolve/{Revision}";
    private const string SpaceDescription = "the Supertonic 3 model";
    private const int HashBufferSize = 1024 * 1024;
    private static readonly TimeSpan s_idleTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<SupertonicAssetFile> _files;
    private readonly string _baseUrl;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, long?>? _spaceProbe;
    private readonly ConcurrentDictionary<string, (long Length, long Ticks)> _verified = new(StringComparer.Ordinal);

    public SupertonicAssetManager(string assetRoot)
        : this(
            assetRoot,
            // HttpClient.Timeout spans the streamed body, so it is only a ceiling; ConnectTimeout
            // bounds a socket that never opens and the downloader's idle watchdog a stalled one.
            new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
            {
                Timeout = TimeSpan.FromHours(2),
            },
            DefaultFiles,
            DefaultBaseUrl,
            spaceProbe: null,
            ownsHttpClient: true)
    {
    }

    internal SupertonicAssetManager(
        string assetRoot,
        HttpClient httpClient,
        IReadOnlyList<SupertonicAssetFile> files,
        string baseUrl,
        Func<string, long?>? spaceProbe = null)
        : this(assetRoot, httpClient, files, baseUrl, spaceProbe, ownsHttpClient: false)
    {
    }

    private SupertonicAssetManager(
        string assetRoot,
        HttpClient httpClient,
        IReadOnlyList<SupertonicAssetFile> files,
        string baseUrl,
        Func<string, long?>? spaceProbe,
        bool ownsHttpClient)
    {
        AssetRoot = assetRoot;
        _httpClient = httpClient;
        _files = files;
        _baseUrl = baseUrl.TrimEnd('/');
        _spaceProbe = spaceProbe;
        _ownsHttpClient = ownsHttpClient;
    }

    public string AssetRoot { get; }

    public long TotalSizeBytes => _files.Sum(file => file.SizeBytes);

    /// <summary>True when every pinned file was verified and has not changed since.</summary>
    public bool AreAssetsReady => _files.All(IsTrusted);

    public bool HasAnyAssets
    {
        get
        {
            try
            {
                return _files.Any(file =>
                {
                    var path = GetPath(file.RelativePath);
                    return File.Exists(path) || File.Exists(path + ".partial") || File.Exists(StampPath(path));
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    /// <summary>
    ///     Trusts files whose stamp still describes them and hashes the rest that have the
    ///     pinned size. Returns whether every file is now verified.
    /// </summary>
    public async Task<bool> VerifyCachedAssetsAsync(CancellationToken ct)
    {
        foreach (var file in _files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsTrusted(file))
                await TryVerifyCachedFileAsync(file, ct).ConfigureAwait(false);
        }

        return AreAssetsReady;
    }

    public async Task DownloadMissingAssetsAsync(IProgress<double>? progress, CancellationToken ct)
    {
        progress?.Report(0);
        Directory.CreateDirectory(AssetRoot);

        // The stable .partial files are shared with any other app instance; serialize writers.
        await using (await AcquireLockAsync(ct).ConfigureAwait(false))
        {
            await VerifyCachedAssetsAsync(ct).ConfigureAwait(false);
            var work = _files.Where(file => !IsTrusted(file)).ToList();

            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- the body deletes files; a query would hide the side effects.
            foreach (var file in work)
            {
                var path = GetPath(file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // A cached file that failed verification would never load; drop it so the
                // space check only has to fit one copy.
                TryDelete(StampPath(path));
                TryDelete(path);
                // Earlier versions staged here without resume.
                TryDelete(path + ".tmp");
            }

            var remaining = work.Select(file => Math.Max(0, file.SizeBytes - PartialLength(file))).ToList();
            DownloadSpace.EnsureAvailable(AssetRoot, remaining.Sum(), SpaceDescription, _spaceProbe);

            var totalBytes = Math.Max(1, work.Sum(file => file.SizeBytes));
            long completedBytes = 0;
            var lastReport = DateTime.MinValue;
            for (var index = 0; index < work.Count; index++)
            {
                var file = work[index];
                var path = GetPath(file.RelativePath);
                var laterBytes = remaining.Skip(index + 1).Sum();
                var completedBefore = completedBytes;
                string? sha256 = null;
                await ResilientDownloader.DownloadToFileAsync(
                    _httpClient,
                    $"{_baseUrl}/{file.RemotePath}?download=true",
                    path,
                    approxTotalBytes: file.SizeBytes,
                    s_idleTimeout,
                    allowResume: true,
                    onBytesOnDisk: onDisk =>
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds < 250)
                            return;

                        lastReport = now;
                        progress?.Report(Math.Min(0.99, (completedBefore + Math.Min(onDisk, file.SizeBytes)) / (double)totalBytes));
                    },
                    verifyComplete: partialPath => sha256 = VerifyDownloaded(file, partialPath),
                    ct,
                    new DownloadSpaceRequirement(SpaceDescription, laterBytes, _spaceProbe)
                ).ConfigureAwait(false);

                Remember(file, sha256!);
                completedBytes += file.SizeBytes;
                progress?.Report(Math.Min(0.99, completedBytes / (double)totalBytes));
            }

            await WriteSourceAsync(ct).ConfigureAwait(false);
        }

        progress?.Report(1.0);
    }

    /// <summary>Deletes every pinned file with its stamp and partial download, then any empty asset folders.</summary>
    public async Task RemoveAssetsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(AssetRoot))
        {
            _verified.Clear();
            return;
        }

        await using (await AcquireLockAsync(ct).ConfigureAwait(false))
        {
            _verified.Clear();
            foreach (var file in _files)
            {
                var path = GetPath(file.RelativePath);
                DeleteOrThrow(StampPath(path));
                DeleteOrThrow(path);
                DeleteOrThrow(path + ".partial");
                DeleteOrThrow(path + ".tmp");
            }

            DeleteOrThrow(GetPath(SupertonicPaths.SourceFileName));
            foreach (var directory in _files
                         .Select(file => Path.GetDirectoryName(GetPath(file.RelativePath))!)
                         .Append(AssetRoot)
                         .Distinct(StringComparer.Ordinal)
                         .OrderByDescending(directory => directory.Length))
            {
                TryDeleteEmptyDirectory(directory);
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private bool IsTrusted(SupertonicAssetFile file)
    {
        if (!_verified.TryGetValue(file.RelativePath, out var stamp))
            return false;

        try
        {
            var info = Describe(GetPath(file.RelativePath));
            return info.Exists && (info.Length, info.LastWriteTimeUtc.Ticks) == stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task TryVerifyCachedFileAsync(SupertonicAssetFile file, CancellationToken ct)
    {
        _verified.TryRemove(file.RelativePath, out _);
        var path = GetPath(file.RelativePath);
        try
        {
            var before = Describe(path);
            if (!before.Exists || before.Length != file.SizeBytes)
                return;

            if (StampMatches(file, path, before))
            {
                _verified[file.RelativePath] = (before.Length, before.LastWriteTimeUtc.Ticks);
                return;
            }

            string sha256;
            await using (var stream = OpenForHashing(path))
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));

            var after = Describe(path);
            if (!string.Equals(sha256, file.Sha256, StringComparison.Ordinal)
                || (after.Length, after.LastWriteTimeUtc) != (before.Length, before.LastWriteTimeUtc))
            {
                return;
            }

            Remember(file, sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not verify Supertonic asset '{file.RelativePath}': {ex.Message}");
        }
    }

    private static bool StampMatches(SupertonicAssetFile file, string path, FileInfo info)
    {
        var stampPath = StampPath(path);
        if (!File.Exists(stampPath))
            return false;

        var parts = File.ReadAllText(stampPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3
            && string.Equals(parts[0], file.Sha256, StringComparison.Ordinal)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && size == info.Length
            && ticks == info.LastWriteTimeUtc.Ticks;
    }

    // Synchronous because the downloader's verifier is; a cancelled hash would discard a complete download.
    private static string VerifyDownloaded(SupertonicAssetFile file, string partialPath)
    {
        var length = new FileInfo(partialPath).Length;
        if (length != file.SizeBytes)
            throw new InvalidDataException(
                $"The downloaded Supertonic asset '{file.RelativePath}' has {length} bytes; expected "
                    + $"{file.SizeBytes}. Please retry the download.");

        string sha256;
        using (var stream = OpenForHashing(partialPath))
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));

        if (!string.Equals(sha256, file.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The downloaded Supertonic asset '{file.RelativePath}' failed its integrity check "
                    + $"(expected SHA-256 {file.Sha256}, got {sha256}). Please retry the download.");

        return sha256;
    }

    private void Remember(SupertonicAssetFile file, string sha256)
    {
        var path = GetPath(file.RelativePath);
        var info = Describe(path);
        WriteStamp(path, sha256, info);
        _verified[file.RelativePath] = (info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static void WriteStamp(string path, string sha256, FileInfo info)
    {
        var stampPath = StampPath(path);
        var temp = stampPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temp,
                string.Create(CultureInfo.InvariantCulture, $"{sha256} {info.Length} {info.LastWriteTimeUtc.Ticks}"));
            File.Move(temp, stampPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a stamp the file is simply hashed again next session.
            Trace.TraceWarning($"Could not record verification of '{path}': {ex.Message}");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task WriteSourceAsync(CancellationToken ct)
    {
        var path = GetPath(SupertonicPaths.SourceFileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var text = string.Join(Environment.NewLine,
            "Supertonic 3 model assets",
            $"Source: {ModelSourceUrl}",
            $"Revision: {Revision}",
            $"License: {SupertonicPaths.LicenseFileName}",
            "The model weights are licensed separately under OpenRAIL-M.",
            "");
        try
        {
            await File.WriteAllTextAsync(temp, text, Encoding.UTF8, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // The lock sits beside the asset folder so removing the folder never deletes a held lock.
    private async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        var lockPath = AssetRoot.TrimEnd(Path.DirectorySeparatorChar) + ".lock";
        try
        {
            return await InterProcessFileLock.AcquireAsync(lockPath, ct).ConfigureAwait(false);
        }
        catch (IOException ex) when (DownloadSpace.TranslateWriteFailure(ex, lockPath, SpaceDescription) is { } noSpace)
        {
            throw noSpace;
        }
    }

    private long PartialLength(SupertonicAssetFile file)
    {
        var partial = new FileInfo(GetPath(file.RelativePath) + ".partial");
        return partial.Exists ? partial.Length : 0;
    }

    private string GetPath(string relativePath) =>
        Path.Join(AssetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string StampPath(string path) => path + ".verified";

    // A symlink's own size and time say nothing about the model; check its final target.
    private static FileInfo Describe(string path)
    {
        var file = new FileInfo(path);
        return file.LinkTarget is null ? file : file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? file;
    }

    private static FileStream OpenForHashing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, HashBufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

    private static void DeleteOrThrow(string path)
    {
        if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Failed to delete Supertonic file '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Failed to remove Supertonic folder '{path}': {ex.Message}");
        }
    }

    internal static IReadOnlyList<SupertonicAssetFile> DefaultFiles { get; } =
    [
        new("onnx/duration_predictor.onnx", "onnx/duration_predictor.onnx", 3700147L, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
        new("onnx/text_encoder.onnx", "onnx/text_encoder.onnx", 36416150L, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
        new("onnx/tts.json", "onnx/tts.json", 8253L, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
        new("onnx/unicode_indexer.json", "onnx/unicode_indexer.json", 277676L, "9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f"),
        new("onnx/vector_estimator.onnx", "onnx/vector_estimator.onnx", 256534781L, "883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c"),
        new("onnx/vocoder.onnx", "onnx/vocoder.onnx", 101424195L, "085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba"),
        new("voice_styles/F1.json", "voice_styles/F1.json", 292046L, "bbdec6ee00231c2c742ad05483df5334cab3b52fda3ba38e6a07059c4563dbc2"),
        new("voice_styles/F2.json", "voice_styles/F2.json", 292423L, "7c722c6a72707b1a77f035d67f0d1351ba187738e06f7683e8c72b1df3477fc6"),
        new("voice_styles/F3.json", "voice_styles/F3.json", 290794L, "12f6ef2573baa2defa1128069cb59f203e3ab67c92af77b42df8a0e3a2f7c6ab"),
        new("voice_styles/F4.json", "voice_styles/F4.json", 291808L, "c2fa764c1225a76dfc3e2c73e8aa4f70d9ee48793860eb34c295fff01c2e032b"),
        new("voice_styles/F5.json", "voice_styles/F5.json", 291479L, "45966e73316415626cf41a7d1c6f3b4c70dbc1ba2bee5c1978ef0ce33244fc8d"),
        new("voice_styles/M1.json", "voice_styles/M1.json", 291748L, "e35604687f5d23694b8e91593a93eec0e4eca6c0b02bb8ed69139ab2ea6b0a5b"),
        new("voice_styles/M2.json", "voice_styles/M2.json", 292055L, "b76cbf62bac707c710cf0ae5aba5e31eea1a6339a9734bfae33ab98499534a50"),
        new("voice_styles/M3.json", "voice_styles/M3.json", 290198L, "ea1ac35ccb91b0d7ecad533a2fbd0eec10c91513d8951e3b25fbba99954e159b"),
        new("voice_styles/M4.json", "voice_styles/M4.json", 291522L, "ca8eefad4fcd989c9379032ff3e50738adc547eeb5e221b82593a6d7b3bac303"),
        new("voice_styles/M5.json", "voice_styles/M5.json", 291469L, "dd22b92740314321f8ae11c5e87f8dd60d060f15dd3a632b5adf77f471f77af2"),
        new(SupertonicPaths.LicenseFileName, "LICENSE", 15007L, "0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f"),
    ];
}
