using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
///     Downloads and unpacks whisper.cpp's NVIDIA CUDA native build on demand. The
///     CPU runtime ships in the Whisper.net.Runtime nuget; the CUDA build's
///     <c>libggml-cuda-whisper.so</c> is ~409 MB — far too large to bundle — so it
///     is fetched at first CUDA use and cached under the plugin's asset directory.
///     <para>
///         The libraries are the exact ones the <c>Whisper.net.Runtime.Cuda.Linux</c>
///         nuget would have bundled; we pull them from that package's canonical,
///         immutable nuget.org artifact (a plain zip) and lay them out as
///         <c>runtimes/cuda/linux-x64/</c> so Whisper.net's own loader finds them
///         once <see cref="Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath" />
///         is pointed here.
///     </para>
/// </summary>
// Not sealed: tests subclass it with a fake that overrides EnsureInstalledAsync (it would
// otherwise download ~167 MB from nuget.org), injected via the plugin's
// SetCudaDependenciesForTests seam.
internal class WhisperCudaRuntimeInstaller
{
    // Must stay in lock-step with the Whisper.net / Whisper.net.Runtime package
    // version referenced by the csproj — whisper.cpp's native ABI is not stable
    // across releases, and a mismatched CUDA runtime would fail to load or crash.
    internal const string RuntimeVersion = "1.8.1";

    private const string PackageId = "whisper.net.runtime.cuda.linux";

    // The canonical, immutable package artifact on nuget.org's flat container.
    internal static readonly string DownloadUrl =
        $"https://api.nuget.org/v3-flatcontainer/{PackageId}/{RuntimeVersion}/"
        + $"{PackageId}.{RuntimeVersion}.nupkg";

    // SHA-256 of that .nupkg, verified against nuget's own published sha512 sidecar.
    // Guards the on-demand download against corruption or tampering before any of
    // its .so files are loaded into the process.
    internal const string PackageSha256 =
        "2c6359b5d489c71f29f9cc9b4a161582c6d84ca0b8a41d791ca2cbde9f2389ee";

    // Used only as a progress denominator when the server omits Content-Length.
    private const long ApproxDownloadBytes = 167_372_419L;

    // Inside the nupkg the libs live under build/linux-x64/; the loader expects
    // them under runtimes/cuda/linux-x64/ relative to LibraryPath.
    private const string PackageLibPrefix = "build/linux-x64/";

    // The set Whisper.net's loader walks for the CUDA runtime (dependencies first,
    // then libwhisper.so). Also the completeness check for IsInstalled.
    private static readonly string[] CoreRuntimeFiles =
    [
        "libggml-base-whisper.so",
        "libggml-cpu-whisper.so",
        "libggml-cuda-whisper.so",
        "libggml-whisper.so",
        "libwhisper.so",
    ];

    private readonly string _runtimeRoot;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperCudaRuntimeInstaller(
        string pluginAssetRoot,
        HttpClient httpClient,
        Action<string>? log = null
    )
    {
        _runtimeRoot = Path.Join(pluginAssetRoot, "Runtimes", "whisper-cuda", RuntimeVersion);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
    }

    /// <summary>
    ///     Directory holding the extracted <c>.so</c> files, in the layout
    ///     Whisper.net's loader walks: <c>&lt;root&gt;/runtimes/cuda/linux-x64/</c>.
    /// </summary>
    public string NativeDirectory => Path.Join(_runtimeRoot, "runtimes", "cuda", "linux-x64");

    /// <summary>
    ///     The value to assign to Whisper.net's
    ///     <see cref="Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath" />. Its
    ///     loader takes <c>Path.GetDirectoryName()</c> of this and appends
    ///     <c>runtimes/cuda/linux-x64</c>, so the leaf name is arbitrary and never
    ///     opened — it only has to make the directory resolve to the runtime root.
    /// </summary>
    public string LibraryPath => Path.Join(_runtimeRoot, "whisper");

    /// <summary>True when every required CUDA library has already been extracted.</summary>
    public bool IsInstalled =>
        CoreRuntimeFiles.All(file => File.Exists(Path.Join(NativeDirectory, file)));

    /// <summary>
    ///     Ensures the CUDA runtime is unpacked, downloading and extracting the
    ///     package if needed. Concurrency-safe and idempotent.
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

            Directory.CreateDirectory(NativeDirectory);

            // Stable staging name (no per-call GUID) so a dropped download's .partial
            // resumes via Range. _gate only serializes this instance, so a cross-process
            // lock guards a second app process sharing the cache (see InterProcessFileLock).
            var nupkgPath = Path.Join(_runtimeRoot, $"{PackageId}.nupkg");

            await using (await InterProcessFileLock
                .AcquireAsync(nupkgPath + ".lock", ct).ConfigureAwait(false))
            {
                // Another process may have finished installing while we waited for the lock.
                if (!IsInstalled)
                {
                    try
                    {
                        _log?.Invoke(
                            $"whisper.cpp GPU runtime: downloading {PackageId} {RuntimeVersion}"
                        );
                        // The helper verifies the SHA-256 before its atomic move, so a
                        // corrupt/tampered download never reaches extraction.
                        await DownloadAsync(nupkgPath, progress, ct).ConfigureAwait(false);

                        _log?.Invoke("whisper.cpp GPU runtime: extracting native libraries");
                        ExtractCoreRuntimeFiles(nupkgPath);
                    }
                    finally
                    {
                        TryDelete(nupkgPath);
                    }
                }
            }

            if (!IsInstalled)
            {
                var missing = CoreRuntimeFiles.Where(
                    f => !File.Exists(Path.Join(NativeDirectory, f))
                );
                throw new InvalidOperationException(
                    "whisper.cpp GPU runtime is incomplete after extraction; missing: "
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
    ///     Deletes the entire whisper.cpp CUDA runtime cache tree
    ///     (<c>…/Runtimes/whisper-cuda</c>, every runtime version) so the next
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
                _log?.Invoke($"whisper.cpp GPU runtime: cleared cache at {root}.");
            }
            catch (Exception ex)
            {
                // Don't swallow: the caller reports "cleared" only when the cache is
                // actually gone, so a corrupt runtime can't masquerade as repaired.
                _log?.Invoke(
                    $"whisper.cpp GPU runtime: failed to clear cache at {root}: {ex.Message}"
                );
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task DownloadAsync(
        string destination,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        // Map the helper's cumulative bytes-on-disk to a progress fraction over the
        // approximate size, throttled to ~4 Hz. MinValue seeds the throttle so the first
        // report (the resume baseline jump) always fires.
        var lastReport = DateTime.MinValue;

        void OnBytesOnDisk(long onDisk)
        {
            var now = DateTime.UtcNow;
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
            verifyComplete: path => VerifySha256(path),
            ct
        );
    }

    // Instance (not static) so the mismatch message can name NativeDirectory — the exact
    // path to delete if a corrupt download needs clearing (M4: a corrupt cached file is
    // never auto-re-fetched, so the user needs to know where it lives).
    private void VerifySha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Checksum mismatch for the {PackageId} package "
                    + $"(expected {PackageSha256}, got {hash.ToLowerInvariant()}). The download may "
                    + $"be corrupt; delete the whisper.cpp GPU runtime cache ({NativeDirectory}) and retry."
            );
    }

    // The nupkg is a zip. Copy out only the linux-x64 .so files, flattening away
    // the package's build/linux-x64/ prefix into the runtime directory.
    private void ExtractCoreRuntimeFiles(string nupkgPath)
    {
        var wanted = new HashSet<string>(CoreRuntimeFiles, StringComparer.Ordinal);

        using var archive = ZipFile.OpenRead(nupkgPath);
        foreach (var entry in archive.Entries.Where(e =>
            e.FullName.StartsWith(PackageLibPrefix, StringComparison.Ordinal)
            && wanted.Contains(Path.GetFileName(e.FullName))))
        {
            var fileName = Path.GetFileName(entry.FullName);
            var destination = Path.Join(NativeDirectory, fileName);
            // Atomic publish so a half-written .so can't be picked up by a concurrent
            // load or a later run's existence check.
            var stagePath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                entry.ExtractToFile(stagePath, overwrite: true);
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
