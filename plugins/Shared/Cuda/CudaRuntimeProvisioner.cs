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

    private static readonly CudaWheel[] WhisperWheels = [CudaRuntime, Cublas];

    private static readonly CudaWheel[] OnnxRuntimeWheels =
        [CudaRuntime, Cublas, Cufft, Curand, Cudnn];

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

        var wheels = profile == CudaRuntimeProfile.WhisperCublas
            ? WhisperWheels
            : OnnxRuntimeWheels;

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
        // can be weighted by real byte totals across the whole batch.
        var jobs = new List<(CudaWheel Wheel, string Url, long Size, string? Sha256)>();
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
            await DownloadAndExtractWheelAsync(
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
            completedBytes += size;
        }

        progress?.Report(1.0);
    }

    private async Task<(string Url, long Size, string? Sha256)> ResolveWheelAsync(
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

        foreach (var entry in json.RootElement.GetProperty("urls").EnumerateArray())
        {
            if (entry.TryGetProperty("packagetype", out var pkgType)
                && pkgType.GetString() != "bdist_wheel")
                continue;

            var filename = entry.GetProperty("filename").GetString();
            // The single linux x64 wheel: a manylinux build for x86_64. Excludes
            // win_amd64 and aarch64. The exact glibc tag (2_17 vs 2_27) varies per
            // package, so match on the platform family rather than a fixed tag.
            if (filename is null
                || !filename.EndsWith(".whl", StringComparison.Ordinal)
                || !filename.Contains("manylinux", StringComparison.Ordinal)
                || !filename.Contains("x86_64", StringComparison.Ordinal)
                || filename.Contains("aarch64", StringComparison.Ordinal))
                continue;

            var url = entry.GetProperty("url").GetString()
                ?? throw new InvalidOperationException($"No URL for {wheel.Package} wheel.");
            var size = entry.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
            string? sha256 = null;
            if (entry.TryGetProperty("digests", out var digests)
                && digests.TryGetProperty("sha256", out var shaNode))
                sha256 = shaNode.GetString();
            return (url, size, sha256);
        }

        throw new InvalidOperationException(
            $"No manylinux x86_64 wheel found for {wheel.Package} {wheel.Version}."
        );
    }

    private async Task DownloadAndExtractWheelAsync(
        CudaWheel wheel,
        string url,
        string? expectedSha256,
        Action<long> onBytesRead,
        CancellationToken ct
    )
    {
        var tmpPath = Path.Join(
            CacheDirectory,
            $"{wheel.Package}.{Guid.NewGuid():N}.whl.tmp"
        );

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
                long readTotal = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    readTotal += read;
                    onBytesRead(readTotal);
                }
            }

            // Guard against a corrupt/truncated download surfacing later as a
            // confusing native load error.
            if (!string.IsNullOrEmpty(expectedSha256))
                VerifySha256(tmpPath, expectedSha256, wheel.Package);

            ExtractSharedObjects(tmpPath);

            // Stamp completion only after every .so is extracted. A wheel like cuDNN
            // ships its primary soname (libcudnn.so.9) alongside companion engine
            // libs; without this marker a crash mid-extract would leave the primary
            // on disk and the next run's IsWheelSatisfied would wrongly skip the
            // re-download, then fail when cuDNN dlopens a missing companion.
            WriteCompletionMarker(wheel);
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

    private static void VerifySha256(string path, string expected, string package)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Checksum mismatch for the {package} wheel "
                    + $"(expected {expected}, got {hash.ToLowerInvariant()}). The download may be "
                    + "corrupt; clear the CUDA runtime cache and retry."
            );
    }

    // A wheel is a zip. The CUDA libs live under nvidia/<component>/lib/*.so* —
    // pull every shared object out flat into the cache dir, ignoring everything
    // else (Python stubs, headers, metadata).
    private void ExtractSharedObjects(string wheelPath)
    {
        using var archive = ZipFile.OpenRead(wheelPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;

            var fileName = Path.GetFileName(entry.FullName);
            if (!IsSharedObject(fileName)
                || !entry.FullName.Contains("/lib/", StringComparison.Ordinal))
                continue;

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
            foreach (var soname in wheel.RequiredSonames)
            {
                if (_preloaded.Contains(soname))
                    continue;

                // Resolve to the absolute path of the file we actually found (cache
                // first, then the system dirs). A toolkit dir like /usr/local/cuda
                // is often absent from ldconfig/LD_LIBRARY_PATH, so a bare-soname
                // dlopen would fail even though IsWheelSatisfied saw the file there.
                // Only fall back to the bare soname (ldconfig resolution) when the
                // file isn't found at a known path.
                var target = ResolveLibraryPath(soname) ?? soname;
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
    // else null (meaning: let the dynamic linker resolve the bare soname).
    private string? ResolveLibraryPath(string soname)
    {
        var cachePath = Path.Join(CacheDirectory, soname);
        if (File.Exists(cachePath))
            return cachePath;

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

            foreach (var dir in parent.EnumerateDirectories())
            {
                if (!string.Equals(dir.Name, BundleVersion, StringComparison.Ordinal))
                {
                    try { dir.Delete(recursive: true); }
                    catch { /* best effort cleanup */ }
                }
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
