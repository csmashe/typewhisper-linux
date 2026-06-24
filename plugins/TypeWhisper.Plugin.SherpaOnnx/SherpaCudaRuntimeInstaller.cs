using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using SharpCompress.Readers;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
///     Downloads and unpacks the k2-fsa sherpa-onnx <em>GPU</em> native build on
///     demand. The CPU runtime ships in the managed nuget; the GPU runtime
///     (~224 MB compressed, with a 368 MB ORT CUDA provider) is far too large to
///     bundle, so it is fetched at first CUDA use and cached under the plugin's
///     asset directory.
/// </summary>
internal sealed class SherpaCudaRuntimeInstaller
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
    internal static readonly string[] CoreRuntimeFiles =
    [
        "libsherpa-onnx-c-api.so",
        "libsherpa-onnx-cxx-api.so",
        "libonnxruntime.so",
        "libonnxruntime_providers_shared.so",
        "libonnxruntime_providers_cuda.so"
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
    public async Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct)
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

            var tarballPath = Path.Join(
                _runtimeRoot,
                $"{AssetFileName}.{Guid.NewGuid():N}.tmp"
            );

            try
            {
                _log?.Invoke($"sherpa-onnx GPU runtime: downloading {AssetFileName}");
                await DownloadAsync(tarballPath, progress, ct).ConfigureAwait(false);

                // Verify before extracting so a corrupt/tampered download can't drop
                // bad native .so files into the cache — code we then dlopen and trust
                // on every later run.
                VerifySha256(tarballPath, RuntimeDirectory);

                _log?.Invoke("sherpa-onnx GPU runtime: extracting native libraries");
                ExtractCoreRuntimeFiles(tarballPath);
            }
            finally
            {
                TryDelete(tarballPath);
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
    ///     same gate as install so it can't race an in-flight extraction. A missing cache
    ///     is a no-op; a delete failure is logged and rethrown so the caller can surface
    ///     it rather than report a corrupt runtime as repaired.
    /// </summary>
    public void ClearCache()
    {
        _gate.Wait();
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

    private async Task DownloadAsync(string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _httpClient
            .GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? ApproxDownloadBytes;

        await using var contentStream = await response.Content
            .ReadAsStreamAsync(ct)
            .ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true
        );

        var buffer = new byte[81920];
        long readTotal = 0;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;

            var now = DateTime.UtcNow;
            if (totalBytes > 0 && (now - lastReport).TotalMilliseconds > 250)
            {
                progress?.Report(Math.Min(1.0, (double)readTotal / totalBytes));
                lastReport = now;
            }
        }
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
