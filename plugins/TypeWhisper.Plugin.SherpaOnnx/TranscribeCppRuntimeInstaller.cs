using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
///     Downloads and unpacks transcribe.cpp's Linux CPU + Vulkan runtime on the first Vulkan load. It is
///     about 22 MB compressed and 63 MB unpacked, so it is fetched on demand rather than bundled.
/// </summary>
// Not sealed: tests subclass it with a fake that overrides EnsureInstalledAsync.
internal class TranscribeCppRuntimeInstaller
{
    internal const string AssetFileName = "transcribe-native-0.3.1-linux-x86_64-cpu-vulkan.tar.gz";

    private const string DownloadUrl =
        "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.3.1/" + AssetFileName;

    // GitHub's published asset digest, matched by a local download.
    private const string AssetSha256 = "7656cd88b563d79af4e483400aebd149c50aad2c3b20ced1f5a06ce1e50f435a";
    internal const long AssetSize = 21_633_824;

    private const string ArchiveRoot = "transcribe-native-linux-x86_64-cpu-vulkan";

    // The archive unpacks to about 63 MB; anything far beyond that is not the pinned archive.
    private const long MaximumExtractedBytes = 200_000_000;
    private const long ExtractedRuntimeBytes = 64_000_000;
    private const string SpaceDescription = "the transcribe.cpp Vulkan runtime";

    // ggml picks one of the CPU variants at start; the plain x64 one runs everywhere.
    private static readonly string[] s_coreRuntimeFiles =
    [
        "contract.json",
        TranscribeCppNative.LibraryFileName,
        "libggml.so",
        "libggml-base.so",
        "libggml-vulkan.so",
        "libggml-cpu-x64.so",
    ];

    private readonly string _runtimeRoot;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TranscribeCppRuntimeInstaller(string pluginDataRoot, HttpClient httpClient, Action<string>? log = null)
    {
        _runtimeRoot = Path.Join(pluginDataRoot, "Runtimes", "transcribe-cpp", "v" + TranscribeCppNative.Version);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
    }

    /// <summary>Directory containing the extracted runtime.</summary>
    public string RuntimeDirectory => Path.Join(_runtimeRoot, "native");

    private bool IsInstalled => s_coreRuntimeFiles.All(file => File.Exists(Path.Join(RuntimeDirectory, file)));

    /// <summary>Downloads and unpacks the runtime if needed. Concurrency-safe and idempotent.</summary>
    public virtual async Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (IsInstalled)
        {
            progress?.Report(1.0);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_runtimeRoot);
            var tarballPath = Path.Join(_runtimeRoot, AssetFileName);

            // A second app process shares this cache; it may finish while we wait.
            await using (await InterProcessFileLock.AcquireAsync(tarballPath + ".lock", ct).ConfigureAwait(false))
            {
                if (!IsInstalled)
                {
                    try
                    {
                        _log?.Invoke($"transcribe.cpp runtime: downloading {AssetFileName}");
                        await DownloadAsync(tarballPath, progress, ct).ConfigureAwait(false);
                        _log?.Invoke("transcribe.cpp runtime: extracting native libraries");
                        try
                        {
                            await InstallFromArchiveAsync(tarballPath, RuntimeDirectory, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                            when (DownloadSpace.TranslateWriteFailure(ex, _runtimeRoot, SpaceDescription) is { } full
                                  && !ReferenceEquals(full, ex))
                        {
                            throw full;
                        }
                    }
                    finally
                    {
                        TryDelete(tarballPath);
                    }
                }
            }

            if (!IsInstalled)
                throw new InvalidOperationException(
                    "The transcribe.cpp runtime is incomplete after extraction; missing: "
                        + string.Join(", ", s_coreRuntimeFiles.Where(f => !File.Exists(Path.Join(RuntimeDirectory, f))))
                );
            progress?.Report(1.0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Unpacks the archive into a staging directory beside <paramref name="destination" /> and moves it
    ///     into place only when complete, so a process that dies mid-extraction leaves no half runtime.
    /// </summary>
    internal static async Task InstallFromArchiveAsync(string archive, string destination, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(archive, staging, ct).ConfigureAwait(false);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    ///     Copies the runtime's top-level <c>.so</c> files, <c>contract.json</c> and its license texts from
    ///     below <see cref="ArchiveRoot" />. Rejects absolute, parent-relative and non-regular entries.
    /// </summary>
    private static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        await using var input = File.OpenRead(archive);
        await using var decompressed = new GZipStream(input, CompressionMode.Decompress);
        await using var tar = new TarReader(decompressed);
        long expanded = 0;
        const string prefix = ArchiveRoot + "/";
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var key = entry.Name;
            if (
                key.StartsWith('/')
                || key.Split('/').Contains("..")
                || entry.EntryType
                    is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            )
                throw new InvalidDataException("Unsafe transcribe.cpp archive entry: " + key);
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExtractedBytes)
                throw new InvalidDataException("The transcribe.cpp archive exceeds its extraction limit.");
            if (entry.EntryType == TarEntryType.Directory || !key.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var relative = key[prefix.Length..];
            var keep =
                relative.StartsWith("licenses/", StringComparison.Ordinal)
                || (!relative.Contains('/')
                    && (relative == "contract.json" || relative.EndsWith(".so", StringComparison.Ordinal)));
            if (!keep || entry.DataStream is null)
                continue;
            var path = Path.Join(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await entry.DataStream.CopyToAsync(output, ct).ConfigureAwait(false);
            if (output.Length != entry.Length)
                throw new InvalidDataException("Incomplete transcribe.cpp archive entry: " + key);
        }
    }

    private Task DownloadAsync(string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var lastReport = DateTime.MinValue;
        return ResilientDownloader.DownloadToFileAsync(
            _httpClient,
            DownloadUrl,
            destination,
            approxTotalBytes: AssetSize,
            idleTimeout: TimeSpan.FromSeconds(60),
            allowResume: true,
            onBytesOnDisk: onDisk =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds <= 250)
                    return;
                progress?.Report(Math.Min(1.0, (double)onDisk / AssetSize));
                lastReport = now;
            },
            verifyComplete: VerifyArchive,
            ct,
            new DownloadSpaceRequirement(SpaceDescription, ExtractedRuntimeBytes)
        );
    }

    // Fails closed before extraction: these libraries are loaded into the worker process.
    internal static void VerifyArchive(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != AssetSize)
            throw new InvalidDataException($"The {AssetFileName} download is {stream.Length} bytes; expected {AssetSize}.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(hash, AssetSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Checksum mismatch for {AssetFileName}; the download was not installed.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
