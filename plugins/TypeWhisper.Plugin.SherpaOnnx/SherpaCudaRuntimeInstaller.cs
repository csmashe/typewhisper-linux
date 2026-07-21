// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Security.Cryptography;
using SharpCompress.Readers;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
///     Downloads and unpacks the k2-fsa sherpa-onnx <em>GPU</em> native build on
///     demand. The CPU runtime ships in the managed nuget; the GPU runtime
///     (~224 MB compressed, with a 368 MB ORT CUDA provider) is far too large to
///     bundle, so it is fetched at first CUDA use and cached under the plugin's
///     asset directory.
/// </summary>
// Not sealed: tests subclass it with a fake that overrides EnsureInstalledAsync (it would
// otherwise download ~224 MB from GitHub), injected via the plugin's
// SetCudaDependenciesForTests seam.
internal class SherpaCudaRuntimeInstaller
{
    // Must stay in lock-step with the managed org.k2fsa.sherpa.onnx package version
    // referenced by the csproj — the C API is not ABI-stable across releases.
    internal const string RuntimeVersion = "v1.12.23";

    internal const string AssetFileName =
        "sherpa-onnx-v1.12.23-cuda-12.x-cudnn-9.x-linux-x64-gpu.tar.bz2";

    internal const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.12.23/" + AssetFileName;

    // SHA-256 of that release asset, matching GitHub's published asset digest (and a
    // local recompute of the served bytes). These .so files get dlopen'd into the
    // TypeWhisper process and trusted on every later run, so guard the on-demand
    // download against corruption or a swapped/tampered release asset before any of
    // them is extracted — fail closed on mismatch. Mirrors the whisper.cpp and shared
    // CUDA wheel paths, which both verify before extraction.
    internal const string AssetSha256 =
        "ac5400eb7971b7134d03429727ebdd702c23597e3721f4a3ade84815708d8c3e";

    // Approximate download size, used only as a progress denominator when the
    // server omits Content-Length.
    private const long ApproxDownloadBytes = 234_120_435L;

    // What we keep out of the tarball's lib/ directory. The TensorRT provider
    // (libonnxruntime_providers_tensorrt.so) is deliberately excluded — we only
    // use the CUDA execution provider, and it adds nothing but bulk.
    // internal (not private) so a regression test can assert the CUDA provider is
    // extracted here even though it must never be preloaded (see SherpaOnnxNativeRuntime).
    // ReSharper disable once InconsistentNaming -- internal static field is part of the test-observable API; PascalCase intended.
    internal static readonly string[] CoreRuntimeFiles =
    [
        "libsherpa-onnx-c-api.so",
        "libsherpa-onnx-cxx-api.so",
        "libonnxruntime.so",
        "libonnxruntime_providers_shared.so",
        "libonnxruntime_providers_cuda.so",
    ];

    private readonly string _runtimeRoot;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SherpaCudaRuntimeInstaller(
        string pluginDataRoot,
        HttpClient httpClient,
        Action<string>? log = null
    )
    {
        _runtimeRoot = Path.Join(pluginDataRoot, "Runtimes", "sherpa-onnx-cuda", RuntimeVersion);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
    }

    /// <summary>Directory containing the extracted GPU <c>.so</c> files.</summary>
    public string RuntimeDirectory => Path.Join(_runtimeRoot, "native");

    /// <summary>True when every required GPU library has already been extracted.</summary>
    public bool IsInstalled =>
        CoreRuntimeFiles.All(file => File.Exists(Path.Join(RuntimeDirectory, file)));

    /// <summary>
    ///     Ensures the GPU runtime is unpacked, downloading and extracting the
    ///     tarball if needed. Concurrency-safe and idempotent.
    /// </summary>
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
            if (IsInstalled)
            {
                progress?.Report(1.0);
                return;
            }

            Directory.CreateDirectory(RuntimeDirectory);

            // Stable staging name (no per-call GUID) so a dropped download's .partial
            // resumes via Range. _gate only serializes this instance, so a cross-process
            // lock guards a second app process sharing the cache (see InterProcessFileLock).
            var tarballPath = Path.Join(_runtimeRoot, AssetFileName);

            await using (await InterProcessFileLock
                .AcquireAsync(tarballPath + ".lock", ct).ConfigureAwait(false))
            {
                // Another process may have finished installing while we waited for the lock.
                if (!IsInstalled)
                {
                    try
                    {
                        _log?.Invoke($"sherpa-onnx GPU runtime: downloading {AssetFileName}");
                        // The helper verifies the SHA-256 before its atomic move, so a
                        // corrupt/tampered download never reaches extraction.
                        await DownloadAsync(tarballPath, progress, ct).ConfigureAwait(false);

                        _log?.Invoke("sherpa-onnx GPU runtime: extracting native libraries");
                        ExtractCoreRuntimeFiles(tarballPath);
                    }
                    finally
                    {
                        TryDelete(tarballPath);
                    }
                }
            }

            if (!IsInstalled)
            {
                var missing = CoreRuntimeFiles.Where(
                    f => !File.Exists(Path.Join(RuntimeDirectory, f))
                );
                throw new InvalidOperationException(
                    "sherpa-onnx GPU runtime is incomplete after extraction; missing: "
                        + string.Join(", ", missing)
                );
            }

            progress?.Report(1.0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Deletes the entire sherpa-onnx GPU runtime cache tree
    ///     (<c>…/Runtimes/sherpa-onnx-cuda</c>, every runtime version) so the next
    ///     <see cref="EnsureInstalledAsync" /> re-downloads from scratch. Guarded by the
    ///     same gate as install so it can't race an in-flight extraction — and awaits the
    ///     gate with <paramref name="ct" /> so a cancel isn't stuck behind it. A missing
    ///     cache is a no-op; a delete failure is logged and rethrown so the caller can
    ///     surface it rather than report a corrupt runtime as repaired.
    /// </summary>
    public async Task ClearCacheAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = Directory.GetParent(_runtimeRoot)?.FullName;
            if (root is null || !Directory.Exists(root))
                return;

            try
            {
                Directory.Delete(root, recursive: true);
                _log?.Invoke($"sherpa-onnx GPU runtime: cleared cache at {root}.");
            }
            catch (Exception ex)
            {
                // Don't swallow: the caller reports "cleared" only when the cache is
                // actually gone, so a corrupt runtime can't masquerade as repaired.
                _log?.Invoke(
                    $"sherpa-onnx GPU runtime: failed to clear cache at {root}: {ex.Message}"
                );
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task DownloadAsync(string destination, IProgress<double>? progress, CancellationToken ct)
    {
        // Map the helper's cumulative bytes-on-disk to a progress fraction over the
        // approximate size, throttled to ~4 Hz. MinValue seeds the throttle so the first
        // report (the resume baseline jump) always fires.
        var lastReport = DateTime.MinValue;

        void OnBytesOnDisk(long onDisk)
        {
            var now = DateTime.UtcNow;
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if ((now - lastReport).TotalMilliseconds > 250)
            {
                progress?.Report(Math.Min(1.0, (double)onDisk / ApproxDownloadBytes));
                lastReport = now;
            }
        }

        return ResilientDownloader.DownloadToFileAsync(
            _httpClient,
            DownloadUrl,
            destination,
            approxTotalBytes: ApproxDownloadBytes,
            idleTimeout: TimeSpan.FromSeconds(60),
            allowResume: true,
            onBytesOnDisk: OnBytesOnDisk,
            verifyComplete: path => VerifySha256(path, RuntimeDirectory),
            ct
        );
    }

    // internal (not private) so a unit test can pin the fail-closed contract without
    // a network download — a corrupt/swapped artifact must throw before extraction.
    // cacheDirectory is optional so the test can call it without a path; the real caller
    // passes RuntimeDirectory so the message names the exact dir to delete (M4).
    internal static void VerifySha256(string path, string? cacheDirectory = null)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Checksum mismatch for {AssetFileName} "
                    + $"(expected {AssetSha256}, got {hash.ToLowerInvariant()}). The download may "
                    + (cacheDirectory is null
                        ? "be corrupt; clear the sherpa-onnx GPU runtime cache and retry."
                        : $"be corrupt; delete the sherpa-onnx GPU runtime cache ({cacheDirectory}) and retry.")
            );
    }

    // SharpCompress ReaderFactory streams a .tar.bz2 in a single forward pass
    // (ArchiveFactory would need random access bzip2 doesn't give us). We copy out
    // only the core files, flattening away the archive's top-level directory.
    private void ExtractCoreRuntimeFiles(string tarballPath)
    {
        var wanted = new HashSet<string>(CoreRuntimeFiles, StringComparer.Ordinal);

        using var stream = File.OpenRead(tarballPath);
        using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory || reader.Entry.Key is null)
                continue;

            var fileName = Path.GetFileName(reader.Entry.Key);
            if (!wanted.Contains(fileName))
                continue;

            var destination = Path.Join(RuntimeDirectory, fileName);
            var stagePath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var entryStream = reader.OpenEntryStream())
                using (var output = new FileStream(
                    stagePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None
                ))
                {
                    entryStream.CopyTo(output);
                }

                File.Move(stagePath, destination, overwrite: true);
            }
            finally
            {
                TryDelete(stagePath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
