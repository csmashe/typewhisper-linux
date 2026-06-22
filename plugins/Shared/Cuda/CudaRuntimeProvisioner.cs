using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TypeWhisper.Plugins.Shared.Cuda;

/// <summary>
///     Which set of CUDA math libraries an engine needs preloaded before its
///     native runtime loads.
/// </summary>
public enum CudaRuntimeProfile
{
    /// <summary>cudart + cuBLAS — what whisper.cpp's CUDA build links against.</summary>
    WhisperCublas,

    /// <summary>
    ///     The full ONNX Runtime CUDA execution-provider dependency set
    ///     (cudart, cuBLAS/cuBLASLt, cuFFT, cuRAND, cuDNN). Required by
    ///     sherpa-onnx's GPU build.
    /// </summary>
    OnnxRuntimeCuda
}

/// <summary>
///     Shared, on-demand provisioner for the NVIDIA CUDA 12 user-space math
///     libraries. It detects which <c>.so</c> files the host already has,
///     downloads only the missing ones as NVIDIA manylinux pip wheels, and
///     <c>dlopen</c>s the full set <c>RTLD_GLOBAL</c> (in dependency order) so a
///     no-rpath native runtime (the ORT CUDA provider, whisper.cpp's CUDA build)
///     can resolve their symbols.
///     <para>
///         GPU binaries are never bundled into the app packages — they are
///         fetched here at first CUDA use and cached under
///         <c>~/.local/share/TypeWhisper/Runtimes/cuda/&lt;BundleVersion&gt;</c>.
///     </para>
/// </summary>
public sealed class CudaRuntimeProvisioner
{
    // Bump when the wheel set/versions change; stale sibling dirs are pruned so a
    // bad or superseded cache can't surface as a confusing native load error.
    internal const string BundleVersion = "cuda12-v1";

    private const int RtldNow = 0x002;
    private const int RtldGlobal = 0x100;

    // Each wheel maps a PyPI package@version to the sonames it must contribute.
    // RequiredSonames are the libraries we both (a) check to decide whether the host
    // already satisfies the wheel and (b) dlopen RTLD_GLOBAL so the no-rpath ORT
    // CUDA provider resolves their symbols. We deliberately do NOT enumerate every
    // companion .so a wheel ships — extraction pulls them all out flat, and they
    // resolve via the libraries' $ORIGIN runpath (see Cudnn below). Listing exact
    // companions would couple us to a wheel's internal layout, which varies by
    // version (e.g. cuDNN 9.x adds/removes engine sub-libs).
    private static readonly CudaWheel CudaRuntime = new(
        "nvidia-cuda-runtime-cu12",
        "12.9.79",
        RequiredSonames: ["libcudart.so.12"]
    );

    private static readonly CudaWheel Cublas = new(
        "nvidia-cublas-cu12",
        "12.9.2.10",
        RequiredSonames: ["libcublasLt.so.12", "libcublas.so.12"]
    );

    private static readonly CudaWheel Cufft = new(
        "nvidia-cufft-cu12",
        "11.4.1.4",
        RequiredSonames: ["libcufft.so.11"]
    );

    private static readonly CudaWheel Curand = new(
        "nvidia-curand-cu12",
        "10.3.10.19",
        RequiredSonames: ["libcurand.so.10"]
    );

    private static readonly CudaWheel Cudnn = new(
        "nvidia-cudnn-cu12",
        "9.22.0.52",
        // Only the dispatcher is required. It dlopens its engine sub-libraries
        // (graph/ops/cnn/adv/engines_*/heuristic, whichever this version ships) at
        // runtime and finds them via its $ORIGIN runpath — and since we extract
        // every wheel .so flat into one cache dir, $ORIGIN is exactly that dir.
        RequiredSonames: ["libcudnn.so.9"]
    );

    // cuDNN 9's graph engine JIT-compiles some kernels and dlopens libnvrtc.so.12
    // LAZILY at that point (it carries an $ORIGIN/../../cuda_nvrtc/lib runpath that
    // doesn't exist in our flat cache, and no library hard-NEEDs nvrtc). Without it,
    // a model/shape that routes through a runtime-compiled cuDNN engine (e.g. Canary's
    // attention path) fails deep in ORT session execution with an opaque error — the
    // conv-heavy Parakeet graph happens to use only precompiled engines, which is why
    // it works without this. Preloading nvrtc RTLD_GLOBAL satisfies that lazy dlopen.
    // Pinned to the CUDA 12.9.1 nvrtc that pairs with cudart 12.9.79; its
    // libnvrtc-builtins companion comes along in the flat extraction and resolves via
    // $ORIGIN. Only sherpa-onnx's ORT/cuDNN path needs this, not whisper.cpp.
    private static readonly CudaWheel Nvrtc = new(
        "nvidia-cuda-nvrtc-cu12",
        "12.9.86",
        RequiredSonames: ["libnvrtc.so.12"]
    );

    private static readonly CudaWheel[] WhisperWheels = [CudaRuntime, Cublas];

    private static readonly CudaWheel[] OnnxRuntimeWheels =
        [CudaRuntime, Cublas, Cufft, Curand, Nvrtc, Cudnn];

    private static readonly string[] s_systemLibraryDirectories = BuildSystemLibraryDirectories();

    // CUDA 12.x toolkit locations (matching the host's preflight search) plus the
    // standard system lib dirs. A toolkit under /usr/local/cuda* is frequently NOT
    // registered with ldconfig, so we must be able to find a library here by path
    // and dlopen it by that absolute path rather than by bare soname.
    private static string[] BuildSystemLibraryDirectories()
    {
        var dirs = new List<string>
        {
            "/usr/local/cuda/lib64",
            "/usr/local/cuda/targets/x86_64-linux/lib"
        };
        foreach (var minor in new[] { "9", "8", "7", "6", "5", "4", "3", "2", "1", "0" })
        {
            dirs.Add($"/usr/local/cuda-12.{minor}/lib64");
            dirs.Add($"/usr/local/cuda-12.{minor}/targets/x86_64-linux/lib");
        }
        dirs.AddRange(["/usr/lib64", "/lib64", "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu"]);
        return dirs.ToArray();
    }

    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _preloadSync = new();
    private readonly HashSet<string> _preloaded = new(StringComparer.Ordinal);

    public CudaRuntimeProvisioner(string cacheRoot, HttpClient httpClient, Action<string>? log = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
        CacheDirectory = Path.Join(cacheRoot, BundleVersion);
    }

    /// <summary>Directory holding the downloaded CUDA <c>.so</c> files for this bundle version.</summary>
    public string CacheDirectory { get; }

    /// <summary>
    ///     The shared cache root both local engines use, so the CUDA math libraries
    ///     are downloaded once. Resolves to
    ///     <c>~/.local/share/TypeWhisper/Runtimes/cuda</c>.
    /// </summary>
    public static string DefaultCacheRoot() =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper",
            "Runtimes",
            "cuda"
        );

    private static CudaWheel[] WheelsFor(CudaRuntimeProfile profile) =>
        profile == CudaRuntimeProfile.WhisperCublas ? WhisperWheels : OnnxRuntimeWheels;

    /// <summary>
    ///     True when every CUDA library the <paramref name="profile" /> needs is
    ///     already resolvable — either provided by the host system or sitting
    ///     complete in our cache. Pure inspection: it does NOT probe the driver,
    ///     download, or preload anything, so it is safe to poll from the UI to
    ///     decide whether CUDA can be selected (true) or the runtime still needs
    ///     downloading (false). When only some wheels are satisfied it returns
    ///     false — the partial-install case the download button must still offer.
    /// </summary>
    public bool IsProfileSatisfied(CudaRuntimeProfile profile) =>
        WheelsFor(profile).All(IsWheelSatisfied);

    /// <summary>
    ///     Ensures every CUDA library the <paramref name="profile" /> needs is on
    ///     disk (downloading the missing wheels) and preloaded
    ///     <c>RTLD_GLOBAL</c> in dependency order. A no-op for libraries the host
    ///     already provides and for sonames already preloaded this process.
    /// </summary>
    public async Task EnsureReadyAsync(
        CudaRuntimeProfile profile,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "On-demand CUDA provisioning is only supported on Linux x64."
            );

        // Verify the NVIDIA driver can actually initialize before downloading or
        // dlopen'ing anything. HasCudaGpu (nvidia-smi/-/dev/nvidiactl present) only
        // proves a device exists, not that the driver is usable for THIS CUDA 12
        // runtime — a too-old driver inits-fail. Committing to the GPU path anyway is
        // especially costly for whisper.cpp: its [Cuda]-only loader caches the native
        // library-load FAILURE in a process-wide static and then poisons CPU fallback
        // until restart. Throwing here lets the caller downgrade to CPU cleanly (and
        // before fetching ~1.5 GB of wheels) instead.
        if (!TryInitializeCudaDriver(out var driverError))
            throw new InvalidOperationException(
                $"The NVIDIA CUDA driver is not usable: {driverError}"
            );

        var wheels = WheelsFor(profile);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            PruneStaleBundles();

            // A wheel is fetched unless EVERY library it provides is already
            // resolvable (on-system or in our cache). Checking only the primary
            // soname would let a partial install/cache (e.g. libcublas.so.12 but no
            // libcublasLt.so.12, or a download interrupted mid-extract) masquerade
            // as complete and then fail at native session creation. cuBLAS in
            // particular is a ~580 MB wheel we still skip when the host toolkit
            // already ships its full set.
            var missing = wheels.Where(w => !IsWheelSatisfied(w)).ToList();

            if (missing.Count > 0)
            {
                _log?.Invoke(
                    $"CUDA runtime: fetching {missing.Count} missing package(s): "
                        + string.Join(", ", missing.Select(w => w.Package))
                );
                await DownloadMissingAsync(missing, progress, ct).ConfigureAwait(false);
            }
            else
            {
                _log?.Invoke("CUDA runtime: all required libraries already present.");
                progress?.Report(1.0);
            }

            PreloadAll(wheels);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadMissingAsync(
        IReadOnlyList<CudaWheel> missing,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        // Resolve each wheel's download URL + size + checksum up front so progress
        // can be weighted by real byte totals across the whole batch. The size is a
        // best-effort estimate: PyPI publishes it today, but ResolveWheelAsync defaults
        // it to 0 if a future response omits it, so the denominator could be understated.
        var jobs = new List<(CudaWheel Wheel, string Url, long Size, string Sha256)>();
        foreach (var wheel in missing)
        {
            var (url, size, sha256) = await ResolveWheelAsync(wheel, ct).ConfigureAwait(false);
            jobs.Add((wheel, url, size, sha256));
        }

        var totalBytes = jobs.Sum(j => j.Size);
        long completedBytes = 0;

        foreach (var (wheel, url, size, sha256) in jobs)
        {
            var baseline = completedBytes;
            var downloaded = await DownloadAndExtractWheelAsync(
                wheel,
                url,
                sha256,
                read =>
                {
                    if (totalBytes > 0)
                        progress?.Report(Math.Min(1.0, (double)(baseline + read) / totalBytes));
                },
                ct
            ).ConfigureAwait(false);
            // Advance by the metadata size when known, else by what we actually read,
            // so a wheel whose PyPI size was missing still moves the cumulative counter
            // instead of stalling it at the previous baseline.
            completedBytes += size > 0 ? size : downloaded;
        }

        progress?.Report(1.0);
    }

    private async Task<(string Url, long Size, string Sha256)> ResolveWheelAsync(
        CudaWheel wheel,
        CancellationToken ct
    )
    {
        var metadataUrl = $"https://pypi.org/pypi/{wheel.Package}/{wheel.Version}/json";
        using var response = await _httpClient
            .GetAsync(metadataUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
            .ConfigureAwait(false);

        // The single linux x64 wheel: a manylinux build for x86_64. Excludes
        // win_amd64 and aarch64. The exact glibc tag (2_17 vs 2_27) varies per
        // package, so match on the platform family rather than a fixed tag.
        static bool IsLinuxX64Wheel(JsonElement entry)
        {
            if (entry.TryGetProperty("packagetype", out var pkgType)
                && pkgType.GetString() != "bdist_wheel")
                return false;

            var filename = entry.GetProperty("filename").GetString();
            return filename is not null
                && filename.EndsWith(".whl", StringComparison.Ordinal)
                && filename.Contains("manylinux", StringComparison.Ordinal)
                && filename.Contains("x86_64", StringComparison.Ordinal)
                && !filename.Contains("aarch64", StringComparison.Ordinal);
        }

        foreach (var entry in json.RootElement.GetProperty("urls").EnumerateArray()
            .Where(IsLinuxX64Wheel))
        {
            var url = entry.GetProperty("url").GetString()
                ?? throw new InvalidOperationException($"No URL for {wheel.Package} wheel.");
            var size = entry.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
            // Fail closed: these .so files get dlopen'd RTLD_GLOBAL into the process, so
            // a missing digest must abort rather than silently skip integrity checking.
            // Scope note: this digest is read from the SAME pypi.org JSON as the URL, so
            // it only guards against in-transit corruption or a CDN serving bytes that
            // don't match its own metadata — NOT a forged/MITM'd metadata response (an
            // attacker who could forge the response controls both the URL and the hash).
            // The actual supply-chain trust anchor for the wheels is TLS to pypi.org;
            // unlike the sherpa tarball and whisper nupkg (source-pinned SHA-256), the
            // wheel hashes are intentionally not pinned in source. Mirrors the url
            // fail-closed above.
            var sha256 = entry.GetProperty("digests").GetProperty("sha256").GetString()
                ?? throw new InvalidOperationException(
                    $"No SHA-256 digest for the {wheel.Package} wheel.");
            return (url, size, sha256);
        }

        throw new InvalidOperationException(
            $"No manylinux x86_64 wheel found for {wheel.Package} {wheel.Version}."
        );
    }

    // Returns the number of bytes actually downloaded, so the caller can advance its
    // cumulative progress counter even when PyPI omitted this wheel's metadata size.
    private async Task<long> DownloadAndExtractWheelAsync(
        CudaWheel wheel,
        string url,
        string expectedSha256,
        Action<long> onBytesRead,
        CancellationToken ct
    )
    {
        var tmpPath = Path.Join(
            CacheDirectory,
            $"{wheel.Package}.{Guid.NewGuid():N}.whl.tmp"
        );

        long readTotal = 0;
        try
        {
            using (var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var contentStream = await response.Content
                    .ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);
                await using var fileStream = new FileStream(
                    tmpPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true
                );

                var buffer = new byte[81920];
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    onBytesRead(readTotal);
                }
            }

            // Guard against a corrupt/truncated download surfacing later as a
            // confusing native load error. expectedSha256 is always present
            // (ResolveWheelAsync fails closed when PyPI omits it).
            VerifySha256(tmpPath, expectedSha256, wheel.Package);

            ExtractSharedObjects(tmpPath);

            // Stamp completion only after every .so is extracted. A wheel like cuDNN
            // ships its primary soname (libcudnn.so.9) alongside companion engine
            // libs; without this marker a crash mid-extract would leave the primary
            // on disk and the next run's IsWheelSatisfied would wrongly skip the
            // re-download, then fail when cuDNN dlopens a missing companion.
            WriteCompletionMarker(wheel);

            return readTotal;
        }
        finally
        {
            TryDelete(tmpPath);
        }
    }

    // A small sentinel written after a wheel's full extraction succeeds. Its
    // presence is what lets the cache (as opposed to the host system) count toward
    // IsWheelSatisfied, so a partially-extracted wheel re-downloads.
    private string WheelMarkerPath(CudaWheel wheel) =>
        Path.Join(CacheDirectory, $".{wheel.Package}-{wheel.Version}.complete");

    private bool IsWheelExtractionComplete(CudaWheel wheel) =>
        File.Exists(WheelMarkerPath(wheel));

    private void WriteCompletionMarker(CudaWheel wheel)
    {
        var marker = WheelMarkerPath(wheel);
        var stagePath = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(stagePath, wheel.Version);
            File.Move(stagePath, marker, overwrite: true);
        }
        finally
        {
            TryDelete(stagePath);
        }
    }

    // Instance (not static) so the mismatch message can name CacheDirectory — the exact
    // path to delete if a corrupt download needs clearing (M4: a corrupt cached file is
    // never auto-re-fetched, so the user needs to know where it lives).
    private void VerifySha256(string path, string expected, string package)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Checksum mismatch for the {package} wheel "
                    + $"(expected {expected}, got {hash.ToLowerInvariant()}). The download may be "
                    + $"corrupt; delete the CUDA runtime cache ({CacheDirectory}) and retry."
            );
    }

    // A wheel is a zip. The CUDA libs live under nvidia/<component>/lib/*.so* —
    // pull every shared object out flat into the cache dir, ignoring everything
    // else (Python stubs, headers, metadata).
    private void ExtractSharedObjects(string wheelPath)
    {
        // Keep only the shared objects under nvidia/<component>/lib/, skipping
        // directory entries, Python stubs, headers, and metadata.
        static bool IsLibEntry(ZipArchiveEntry entry) =>
            !entry.FullName.EndsWith("/", StringComparison.Ordinal)
            && entry.FullName.Contains("/lib/", StringComparison.Ordinal)
            && IsSharedObject(Path.GetFileName(entry.FullName));

        using var archive = ZipFile.OpenRead(wheelPath);
        foreach (var entry in archive.Entries.Where(IsLibEntry))
        {
            var fileName = Path.GetFileName(entry.FullName);
            var destination = Path.Join(CacheDirectory, fileName);
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

    private void PreloadAll(IReadOnlyList<CudaWheel> wheels)
    {
        var failures = new List<string>();
        lock (_preloadSync)
        {
            foreach (var wheel in wheels)
            foreach (var soname in wheel.RequiredSonames.Where(s => !_preloaded.Contains(s)))
            {
                // Resolve to the absolute path of the file we actually found (cache
                // first, then the system dirs). A toolkit dir like /usr/local/cuda
                // is often absent from ldconfig/LD_LIBRARY_PATH, so a bare-soname
                // dlopen would fail even though IsWheelSatisfied saw the file there.
                // Only fall back to the bare soname (ldconfig resolution) when the
                // file isn't found at a known path.
                var target = ResolveLibraryPath(wheel, soname) ?? soname;
                var handle = dlopen(target, RtldNow | RtldGlobal);
                if (handle == IntPtr.Zero)
                {
                    var error = Marshal.PtrToStringAnsi(dlerror());
                    failures.Add($"{soname}: {error ?? "unknown error"}");
                }
                else
                {
                    _preloaded.Add(soname);
                }
            }
        }

        // A library we expected (every one is either on-system or freshly
        // downloaded) failing to load means the runtime is not actually usable.
        // Surface it so an explicit CUDA request errors and Auto falls back to CPU,
        // instead of letting a half-loaded runtime fail deep in session creation.
        if (failures.Count > 0)
            throw new InvalidOperationException(
                "Failed to preload required CUDA libraries: " + string.Join("; ", failures)
            );
    }

    // Satisfied only when every library the wheel provides is resolvable; a single
    // missing soname means we (re)download the wheel, which supplies the full set.
    // A library found in OUR cache only counts when the wheel's completion marker is
    // present — otherwise a download interrupted mid-extract (primary soname written,
    // companions not) could masquerade as complete. Libraries the host already ships
    // on the system count regardless: we never wrote (or need) a marker for those.
    private bool IsWheelSatisfied(CudaWheel wheel)
    {
        // Check the marked-complete cache first so a warm cache short-circuits before
        // the (potentially ldconfig-spawning) system probe, matching the prior fast path.
        var extracted = IsWheelExtractionComplete(wheel);
        return wheel.RequiredSonames.All(soname =>
            (extracted && IsInCache(soname)) || IsResolvableOnSystem(soname));
    }

    private bool IsInCache(string soname) => File.Exists(Path.Join(CacheDirectory, soname));

    // Absolute path of the soname if it exists in our cache or a known system dir,
    // else null (meaning: let the dynamic linker resolve the bare soname). The cache
    // copy is only trusted when the wheel's extraction completed (marker present) —
    // mirroring IsWheelSatisfied — so a partial extract (primary soname written,
    // companions/marker missing) can't be preferred over a complete system copy.
    private string? ResolveLibraryPath(CudaWheel wheel, string soname)
    {
        if (IsWheelExtractionComplete(wheel))
        {
            var cachePath = Path.Join(CacheDirectory, soname);
            if (File.Exists(cachePath))
                return cachePath;
        }

        foreach (var dir in EnumerateSystemSearchDirectories())
        {
            try
            {
                var candidate = Path.Join(dir, soname);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore inaccessible directories.
            }
        }

        return null;
    }

    private static bool IsResolvableOnSystem(string soname)
    {
        foreach (var dir in EnumerateSystemSearchDirectories())
        {
            try
            {
                if (File.Exists(Path.Join(dir, soname)))
                    return true;
            }
            catch
            {
                // Ignore inaccessible directories.
            }
        }

        return LdConfigContains(soname);
    }

    private static IEnumerable<string> EnumerateSystemSearchDirectories()
    {
        var ldLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(ldLibraryPath))
        {
            foreach (var dir in ldLibraryPath.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return dir;
            }
        }

        foreach (var dir in s_systemLibraryDirectories)
            yield return dir;
    }

    private static bool LdConfigContains(string soname)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("ldconfig", "-p")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            );

            if (process is null)
                return false;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1000))
            {
                try { process.Kill(true); } catch { /* best effort */ }
                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            errorTask.GetAwaiter().GetResult();
            return output.Contains(soname, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // Remove sibling bundle dirs from earlier BundleVersions so superseded caches
    // don't accumulate (cuDNN alone is ~1.7 GB unpacked).
    private void PruneStaleBundles()
    {
        try
        {
            var parent = Directory.GetParent(CacheDirectory);
            if (parent is null || !parent.Exists)
                return;

            foreach (var dir in parent.EnumerateDirectories()
                .Where(dir => !string.Equals(dir.Name, BundleVersion, StringComparison.Ordinal)))
            {
                try { dir.Delete(recursive: true); }
                catch { /* best effort cleanup */ }
            }
        }
        catch
        {
            // Cleanup is best-effort; never block provisioning on it.
        }
    }

    private static bool IsSharedObject(string fileName) =>
        fileName.EndsWith(".so", StringComparison.Ordinal)
        || fileName.Contains(".so.", StringComparison.Ordinal);

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

    // Probes the NVIDIA driver via cuInit(0) from libcuda.so.1 (the driver stub,
    // never one of our provisioned math libs). Returns false — rather than throwing —
    // when the driver is absent (DllNotFoundException) or can't initialize (cuInit
    // returns a non-zero CUresult, e.g. CUDA_ERROR_NO_DEVICE / a driver-too-old
    // mismatch), so the caller can fall back to CPU without committing to the GPU
    // native runtimes.
    internal static bool TryInitializeCudaDriver(out string? error)
    {
        try
        {
            var result = cuInit(0);
            if (result == 0)
            {
                error = null;
                return true;
            }

            error = $"cuInit returned CUDA error {result}.";
            return false;
        }
        catch (DllNotFoundException)
        {
            error = "the CUDA driver library (libcuda.so.1) was not found.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"the driver probe failed: {ex.Message}";
            return false;
        }
    }

    [DllImport("libcuda.so.1", EntryPoint = "cuInit")]
    private static extern int cuInit(uint flags);

    [DllImport("libdl.so.2", CharSet = CharSet.Ansi)]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlerror();

    private sealed record CudaWheel(
        string Package,
        string Version,
        string[] RequiredSonames
    );
}
