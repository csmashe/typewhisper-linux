// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.IO.Compression;
using System.Runtime.InteropServices;
using TypeWhisper.PluginSDK.Processes;
using System.Text.Json;
using TypeWhisper.Plugins.Shared.Net;

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
    OnnxRuntimeCuda,
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
///         fetched here at first CUDA use and cached under the host-selected
///         shared runtime root.
///     </para>
/// </summary>
// Not sealed: tests subclass it with a fake that overrides EnsureReadyAsync (the dlopen
// half can't run in CI), injected into the plugins via their SetCudaDependenciesForTests
// seam. The download/extract/marker/prune logic is still exercised directly through the
// internal DownloadAndExtractAsync / ExtractSharedObjects / PruneStaleBundles members.
public class CudaRuntimeProvisioner
{
    // Bump when the wheel set/versions change; stale sibling dirs are pruned so a
    // bad or superseded cache can't surface as a confusing native load error.
    internal const string BundleVersion = "cuda12-v1";

    private const int RtldNow = 0x002;
    private const int RtldGlobal = 0x100;
    private static readonly TimeSpan s_defaultMaintenanceLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_provisioningLockAttempt = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan s_provisioningLockRetry = TimeSpan.FromMilliseconds(100);

    // Each wheel maps a PyPI package@version to the sonames it must contribute.
    // RequiredSonames are the libraries we both (a) check to decide whether the host
    // already satisfies the wheel and (b) dlopen RTLD_GLOBAL so the no-rpath ORT
    // CUDA provider resolves their symbols. We deliberately do NOT enumerate every
    // companion .so a wheel ships — extraction pulls them all out flat, and they
    // resolve via the libraries' $ORIGIN runpath (see s_cudnn below). Listing exact
    // companions would couple us to a wheel's internal layout, which varies by
    // version (e.g. cuDNN 9.x adds/removes engine sub-libs).
    private static readonly CudaWheel s_cudaRuntime = new(
        "nvidia-cuda-runtime-cu12",
        "12.9.79",
        RequiredSonames: ["libcudart.so.12"]
    );

    private static readonly CudaWheel s_cublas = new(
        "nvidia-cublas-cu12",
        "12.9.2.10",
        RequiredSonames: ["libcublasLt.so.12", "libcublas.so.12"]
    );

    private static readonly CudaWheel s_cufft = new(
        "nvidia-cufft-cu12",
        "11.4.1.4",
        RequiredSonames: ["libcufft.so.11"]
    );

    private static readonly CudaWheel s_curand = new(
        "nvidia-curand-cu12",
        "10.3.10.19",
        RequiredSonames: ["libcurand.so.10"]
    );

    private static readonly CudaWheel s_cudnn = new(
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
    private static readonly CudaWheel s_nvrtc = new(
        "nvidia-cuda-nvrtc-cu12",
        "12.9.86",
        RequiredSonames: ["libnvrtc.so.12"]
    );

    private static readonly CudaWheel[] s_whisperWheels = [s_cudaRuntime, s_cublas];

    private static readonly CudaWheel[] s_onnxRuntimeWheels =
        [s_cudaRuntime, s_cublas, s_cufft, s_curand, s_nvrtc, s_cudnn];

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
            "/usr/local/cuda/targets/x86_64-linux/lib",
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
    private readonly Lock _preloadSync = new();
    private readonly HashSet<string> _preloaded = new(StringComparer.Ordinal);
    private readonly string _cacheRoot;
    private readonly string _maintenanceLockPath;
    private readonly string _wheelLockDirectory;
    private readonly string _legacyCacheRoot;
    private readonly string _legacyMigrationDisabledPath;
    private readonly Action<string, string> _moveDirectory;
    private readonly Func<IPluginProcessSupervisor>? _processSupervisor;
    private bool _legacyMigrationAttempted;

    public CudaRuntimeProvisioner(
        string cacheRoot,
        HttpClient httpClient,
        Action<string>? log = null,
        Func<IPluginProcessSupervisor>? processSupervisor = null
    )
        : this(
            cacheRoot,
            httpClient,
            log,
            DefaultCacheRoot(),
            Directory.Move,
            processSupervisor
        ) { }

    internal CudaRuntimeProvisioner(
        string cacheRoot,
        HttpClient httpClient,
        Action<string>? log,
        string legacyCacheRoot,
        Action<string, string> moveDirectory,
        Func<IPluginProcessSupervisor>? processSupervisor = null
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
        _moveDirectory = moveDirectory
            ?? throw new ArgumentNullException(nameof(moveDirectory));
        _processSupervisor = processSupervisor;

        var cachePaths = ResolveCachePaths(cacheRoot, nameof(cacheRoot));
        _cacheRoot = cachePaths.CacheRoot;
        _maintenanceLockPath = cachePaths.MaintenanceLockPath;
        _wheelLockDirectory = cachePaths.WheelLockDirectory;
        CacheDirectory = Path.Join(_cacheRoot, BundleVersion);
        var legacyCachePaths = ResolveCachePaths(
            legacyCacheRoot,
            nameof(legacyCacheRoot)
        );
        _legacyCacheRoot = legacyCachePaths.CacheRoot;
        _legacyMigrationDisabledPath = Path.Join(
            Directory.GetParent(_legacyCacheRoot)!.FullName,
            Path.GetFileName(_legacyCacheRoot) + ".legacy-migration-disabled"
        );
    }

    /// <summary>Directory holding the downloaded CUDA <c>.so</c> files for this bundle version.</summary>
    public string CacheDirectory { get; }

    // Test seams: pin the lock paths and timeout so tests avoid a real per-user cache.
    // ReSharper disable once ConvertToAutoPropertyWhenPossible -- the backing field is the real member, read throughout this class; these are read-only test seams over it.
    internal string MaintenanceLockPathForTests => _maintenanceLockPath;
    // ReSharper disable once ConvertToAutoPropertyWhenPossible -- the backing field is the real member, read throughout this class; these are read-only test seams over it.
    internal string WheelLockDirectoryForTests => _wheelLockDirectory;
    // ReSharper disable once ConvertToAutoPropertyWhenPossible -- the backing field is the real member, read throughout this class; this is a read-only test seam over it.
    internal string LegacyMigrationDisabledPathForTests =>
        _legacyMigrationDisabledPath;
    internal TimeSpan MaintenanceLockTimeoutForTests { get; init; } =
        s_defaultMaintenanceLockTimeout;

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
    ///     Resolves the shared CUDA root from a host-provided per-plugin asset
    ///     directory. Host paths have the shape
    ///     <c>&lt;asset-root&gt;/PluginData/&lt;plugin-id&gt;</c>, so walking up
    ///     through the plugin and PluginData directories lets every engine select
    ///     the same <c>&lt;asset-root&gt;/Runtimes/cuda</c> sibling. Older hosts
    ///     and tests that provide no asset directory retain the legacy default.
    /// </summary>
    internal static string CacheRootForPluginAssetDirectory(string? pluginAssetDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginAssetDirectory))
            return DefaultCacheRoot();

        var pluginDirectory = new DirectoryInfo(pluginAssetDirectory);
        var commonAssetRoot = pluginDirectory.Parent?.Parent;
        return commonAssetRoot is null
            ? DefaultCacheRoot()
            : Path.Join(commonAssetRoot.FullName, "Runtimes", "cuda");
    }

    private static CudaWheel[] WheelsFor(CudaRuntimeProfile profile) =>
        profile == CudaRuntimeProfile.WhisperCublas ? s_whisperWheels : s_onnxRuntimeWheels;

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
    public virtual async Task EnsureReadyAsync(
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

        await ProvisionAsync(
                profile,
                progress,
                () =>
                {
                    PreloadAll(WheelsFor(profile));
                    return Task.CompletedTask;
                },
                ct
            )
            .ConfigureAwait(false);
    }

    // The network+disk half of EnsureReadyAsync. Internal so tests can exercise the
    // marker/satisfied/extract/prune/progress/concurrency logic against a fake
    // HttpMessageHandler and a temp cache dir, never touching dlopen or the driver probe.
    internal Task DownloadAndExtractAsync(
        CudaRuntimeProfile profile,
        IProgress<double>? progress,
        CancellationToken ct
    ) =>
        ProvisionAsync(profile, progress, () => Task.CompletedTask, ct);

    // Async callback seam for tests that need to observe or park the final operation
    // performed while the complete profile lock lease is still held.
    internal Task DownloadAndExtractAsync(
        CudaRuntimeProfile profile,
        IProgress<double>? progress,
        Func<Task> runUnderProfileLocks,
        CancellationToken ct
    ) =>
        ProvisionAsync(profile, progress, runUnderProfileLocks, ct);

    private async Task ProvisionAsync(
        CudaRuntimeProfile profile,
        IProgress<double>? progress,
        Func<Task> runUnderProfileLocks,
        CancellationToken ct
    )
    {
        var wheels = WheelsFor(profile);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TryMigrateLegacyCacheAsync(ct).ConfigureAwait(false);
            EnsureExternalLockDirectory();

            // A wheel is fetched unless EVERY library it provides is already
            // resolvable (on-system or in our cache). Checking only the primary
            // soname would let a partial install/cache (e.g. libcublas.so.12 but no
            // libcublasLt.so.12, or a download interrupted mid-extract) masquerade
            // as complete and then fail at native session creation. cuBLAS in
            // particular is a ~580 MB wheel we still skip when the host toolkit
            // already ships its full set.
            List<CudaWheel> missing;
            await using (
                await InterProcessFileLock
                    .AcquireAsync(_maintenanceLockPath, ct)
                    .ConfigureAwait(false)
            )
            {
                Directory.CreateDirectory(CacheDirectory);
                missing = wheels.Where(w => !IsWheelSatisfied(w)).ToList();
            }

            // Resolve initial metadata outside the profile locks: PyPI latency must not
            // extend the usual lock hold time. Clear may run here, so this is only a
            // speculative snapshot that is recomputed after the full lease is acquired.
            var jobs = new Dictionary<
                CudaWheel,
                (string Url, long Size, string Sha256)
            >();
            foreach (var wheel in missing)
            {
                jobs[wheel] = await ResolveWheelAsync(wheel, ct).ConfigureAwait(false);
            }

            int fetchedCount;
            await using (
                await AcquireProvisioningWheelLocksAsync(wheels, ct).ConfigureAwait(false)
            )
            {
                // Clear may have run while PyPI metadata was resolving. Recreate the
                // directory and recompute the entire profile only after every profile
                // wheel is locked, including on the initially-satisfied fast path.
                Directory.CreateDirectory(CacheDirectory);
                missing = wheels.Where(w => !IsWheelSatisfied(w)).ToList();

                // Only wheels that became missing during the pre-lock window need an
                // under-lock metadata request; reuse the speculative jobs for the rest.
                foreach (var wheel in missing.Where(wheel => !jobs.ContainsKey(wheel)))
                    jobs[wheel] = await ResolveWheelAsync(wheel, ct).ConfigureAwait(false);

                fetchedCount = missing.Count;
                if (fetchedCount > 0)
                {
                    _log?.Invoke(
                        $"CUDA runtime: fetching {fetchedCount} missing package(s): "
                            + string.Join(", ", missing.Select(w => w.Package))
                    );
                    await DownloadMissingAsync(missing, jobs, progress, ct)
                        .ConfigureAwait(false);
                }

                if (!wheels.All(IsWheelSatisfied))
                    throw new InvalidOperationException(
                        $"CUDA runtime provisioning did not satisfy the {profile} profile."
                    );

                await runUnderProfileLocks().ConfigureAwait(false);
            }

            _log?.Invoke(
                fetchedCount > 0
                    ? "CUDA runtime: all required libraries are ready."
                    : "CUDA runtime: all required libraries already present."
            );
            progress?.Report(1.0);

            // Pruning is best-effort and deliberately begins only after the profile
            // lease is released and provisioning completion has been reported.
            await PruneStaleBundlesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TryMigrateLegacyCacheAsync(CancellationToken ct)
    {
        if (_legacyMigrationAttempted)
            return;

        if (PathsEqual(_legacyCacheRoot, _cacheRoot))
        {
            _legacyMigrationAttempted = true;
            return;
        }

        if (File.Exists(_legacyMigrationDisabledPath))
        {
            LogLegacyMigrationDisabled();
            _legacyMigrationAttempted = true;
            return;
        }

        if (!Directory.Exists(_legacyCacheRoot)
            || Directory.Exists(_cacheRoot))
        {
            _legacyMigrationAttempted = true;
            return;
        }

        var legacyPaths = ResolveCachePaths(_legacyCacheRoot, nameof(_legacyCacheRoot));
        try
        {
            // Protect the destination against another provisioner creating it while
            // migration waits for the legacy cache. Each maintenance lease owns root
            // -> every external wheel sentinel, so no active or starting provisioning
            // batch can overlap the atomic move.
            await using var destinationLocks = await AcquireMaintenanceLocksAsync(
                    _maintenanceLockPath,
                    _wheelLockDirectory,
                    "migrating the CUDA runtime cache",
                    ct
                )
                .ConfigureAwait(false);

            // Clear may have published the tombstone while migration waited for the
            // destination lease. Check before waiting on any legacy-root locks.
            if (File.Exists(_legacyMigrationDisabledPath))
            {
                LogLegacyMigrationDisabled();
                _legacyMigrationAttempted = true;
                return;
            }

            await using var legacyLocks = await AcquireMaintenanceLocksAsync(
                    legacyPaths.MaintenanceLockPath,
                    legacyPaths.WheelLockDirectory,
                    "migrating the legacy CUDA runtime cache",
                    ct
                )
                .ConfigureAwait(false);

            // Re-check under both roots' complete maintenance leases: another
            // process may have migrated or provisioned while these locks were pending.
            if (!Directory.Exists(_legacyCacheRoot) || Directory.Exists(_cacheRoot))
            {
                _legacyMigrationAttempted = true;
                return;
            }

            _moveDirectory(_legacyCacheRoot, _cacheRoot);
            _log?.Invoke(
                $"CUDA runtime: migrated cache from {_legacyCacheRoot} to {_cacheRoot}."
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best effort only. The old cache is never deleted here; a failed move
            // falls through to normal provisioning at the configured destination.
            _log?.Invoke(
                $"CUDA runtime: could not migrate cache from {_legacyCacheRoot} "
                    + $"to {_cacheRoot}: {ex.Message} Leaving the old cache in place "
                    + "and provisioning at the configured location."
            );
        }

        _legacyMigrationAttempted = true;
    }

    private async Task DownloadMissingAsync(
        IReadOnlyList<CudaWheel> missing,
        IReadOnlyDictionary<CudaWheel, (string Url, long Size, string Sha256)> jobs,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        // Weight progress by metadata sizes for the recomputed set. A size of zero is
        // allowed and advances by bytes actually read instead.
        var totalBytes = missing.Sum(wheel => jobs[wheel].Size);
        long completedBytes = 0;

        foreach (var wheel in missing)
        {
            var (url, size, sha256) = jobs[wheel];
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
    }

    private async Task<ExternalLockLease> AcquireProvisioningWheelLocksAsync(
        IReadOnlyList<CudaWheel> wheels,
        CancellationToken ct
    )
    {
        EnsureExternalLockDirectory();
        // Lock coupling remains root -> stable package sentinel order. The caller passes
        // the complete requested profile, and retains the returned wheel lease through
        // revalidation and preload; root is released once every wheel lock is acquired.
        var lockPaths = wheels
            .Select(WheelLockPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileStream? rootLock = null;
            var wheelLocks = new List<FileStream>(lockPaths.Length);
            try
            {
                rootLock = await InterProcessFileLock
                    .AcquireAsync(_maintenanceLockPath, ct)
                    .ConfigureAwait(false);

                foreach (var lockPath in lockPaths)
                {
                    // Do not sit on the root lock behind a busy wheel: a short
                    // acquire attempt followed by a full retry lets a provisioner
                    // for a disjoint wheel (or maintenance) take the root meanwhile.
                    using var attemptCts =
                        CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(s_provisioningLockAttempt);
                    wheelLocks.Add(
                        await InterProcessFileLock
                            .AcquireAsync(lockPath, attemptCts.Token)
                            .ConfigureAwait(false)
                    );
                }

                return new ExternalLockLease(wheelLocks);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await DisposeLocksAsync(wheelLocks).ConfigureAwait(false);
            }
            catch
            {
                await DisposeLocksAsync(wheelLocks).ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (rootLock is not null)
                    await rootLock.DisposeAsync().ConfigureAwait(false);
            }

            await Task.Delay(s_provisioningLockRetry, ct).ConfigureAwait(false);
        }
    }

    private Task<ExternalLockLease> AcquireMaintenanceLocksAsync(
        string operation,
        CancellationToken ct
    ) =>
        AcquireMaintenanceLocksAsync(
            _maintenanceLockPath,
            _wheelLockDirectory,
            operation,
            ct
        );

    private async Task<ExternalLockLease> AcquireMaintenanceLocksAsync(
        string maintenanceLockPath,
        string wheelLockDirectory,
        string operation,
        CancellationToken ct
    )
    {
        EnsureExternalLockDirectory(wheelLockDirectory);
        using var timeoutCts = new CancellationTokenSource(MaintenanceLockTimeoutForTests);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCts.Token
        );
        var acquired = new List<FileStream>();

        try
        {
            acquired.Add(
                await InterProcessFileLock
                    .AcquireAsync(maintenanceLockPath, linkedCts.Token)
                    .ConfigureAwait(false)
            );

            // Root is held before this enumeration, so no provisioner can add a new
            // wheel sentinel after the snapshot. Include the known wheel set plus
            // existing sentinels, for forward compatibility with bundle/package changes.
            var wheelLockPaths = s_onnxRuntimeWheels
                .Select(wheel => WheelLockPath(wheel, wheelLockDirectory))
                .Concat(Directory.EnumerateFiles(wheelLockDirectory, "*.lock"))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            foreach (var lockPath in wheelLockPaths)
            {
                acquired.Add(
                    await InterProcessFileLock
                        .AcquireAsync(lockPath, linkedCts.Token)
                        .ConfigureAwait(false)
                );
            }

            return new ExternalLockLease(acquired);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            await DisposeLocksAsync(acquired).ConfigureAwait(false);
            throw new TimeoutException(
                $"Timed out waiting for another CUDA cache operation before {operation}.",
                ex
            );
        }
        catch
        {
            await DisposeLocksAsync(acquired).ConfigureAwait(false);
            throw;
        }
    }

    private string WheelLockPath(CudaWheel wheel) =>
        WheelLockPath(wheel, _wheelLockDirectory);

    // Package-scoped sentinels intentionally overlap unchanged wheels across mixed old
    // and new bundle versions. Wheel-set changes can therefore have only partial overlap;
    // maintenance's root + existing-sentinel snapshot remains the compatibility boundary,
    // rather than adding a separate partial-compatibility lock.
    private static string WheelLockPath(CudaWheel wheel, string wheelLockDirectory) =>
        Path.Join(wheelLockDirectory, wheel.Package + ".lock");

    private void EnsureExternalLockDirectory() =>
        EnsureExternalLockDirectory(_wheelLockDirectory);

    private static void EnsureExternalLockDirectory(string wheelLockDirectory) =>
        Directory.CreateDirectory(wheelLockDirectory);

    private static CachePaths ResolveCachePaths(string cacheRoot, string parameterName)
    {
        var cacheRootDirectory = Directory.GetParent(Path.Join(cacheRoot, BundleVersion))
            ?? throw new ArgumentException(
                "The CUDA cache root must have a parent directory.",
                parameterName
            );
        var cacheParent = cacheRootDirectory.Parent
            ?? throw new ArgumentException(
                "The CUDA cache root must not be a filesystem root.",
                parameterName
            );
        return new CachePaths(
            cacheRootDirectory.FullName,
            Path.Join(
                cacheParent.FullName,
                cacheRootDirectory.Name + ".maintenance.lock"
            ),
            Path.Join(
                cacheParent.FullName,
                cacheRootDirectory.Name + ".locks"
            )
        );
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            ),
            StringComparison.Ordinal
        );

    private static async ValueTask DisposeLocksAsync(List<FileStream> locks)
    {
        for (var i = locks.Count - 1; i >= 0; i--)
            await locks[i].DisposeAsync().ConfigureAwait(false);
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
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- local function kept near its point of use for readability.
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

    // Returns the cumulative bytes-on-disk for the wheel (the full file size on a
    // resumed download), so the caller can advance its cumulative progress counter
    // even when PyPI omitted this wheel's metadata size.
    private async Task<long> DownloadAndExtractWheelAsync(
        CudaWheel wheel,
        string url,
        string expectedSha256,
        Action<long> onBytesRead,
        CancellationToken ct
    )
    {
        // Per-package staging name so two wheels never share a .partial and a dropped
        // download resumes via Range. AcquireProvisioningWheelLocksAsync already holds
        // this package's stable EXTERNAL sentinel for the full provisioning batch.
        var wheelPath = Path.Join(CacheDirectory, $"{wheel.Package}.whl");

        // A sibling that held the lock first may have just completed this wheel — re-check
        // so we don't redundantly re-fetch a hundreds-of-MB wheel.
        if (IsWheelSatisfied(wheel))
            return 0;

        long lastOnDisk = 0;
        try
        {
            // The helper verifies the SHA-256 over the completed partial before its
            // atomic move (expectedSha256 is always present — ResolveWheelAsync fails
            // closed when PyPI omits it), so a corrupt/truncated download never reaches
            // extraction. Resume is safe precisely because of that full-file hash.
            await ResilientDownloader.DownloadToFileAsync(
                _httpClient,
                url,
                wheelPath,
                approxTotalBytes: null,
                idleTimeout: TimeSpan.FromSeconds(60),
                allowResume: true,
                onBytesOnDisk: onDisk =>
                {
                    // On resume onDisk is the FULL wheel size (pre-existing + new), so
                    // the caller's baseline math advances the bar to the right place.
                    lastOnDisk = onDisk;
                    onBytesRead(onDisk);
                },
                verifyComplete: path => VerifySha256(path, expectedSha256, wheel.Package),
                ct
            ).ConfigureAwait(false);

            ExtractSharedObjects(wheelPath);

            // Stamp completion only after every .so is extracted. A wheel like cuDNN
            // ships its primary soname (libcudnn.so.9) alongside companion engine
            // libs; without this marker a crash mid-extract would leave the primary
            // on disk and the next run's IsWheelSatisfied would wrongly skip the
            // re-download, then fail when cuDNN dlopens a missing companion.
            WriteCompletionMarker(wheel);

            return lastOnDisk;
        }
        finally
        {
            TryDelete(wheelPath);
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
    // internal so a unit test can assert the /lib/ filter + flatten directly against a
    // synthetic in-memory zip without a network download.
    internal void ExtractSharedObjects(string wheelPath)
    {
        // Keep only the shared objects under nvidia/<component>/lib/, skipping
        // directory entries, Python stubs, headers, and metadata.
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- local function kept near its point of use for readability.
        static bool IsLibEntry(ZipArchiveEntry entry) =>
            !entry.FullName.EndsWith('/')
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

    // Test seam: override the on-system library probe so unit tests get a deterministic
    // "not on system" (or "on system") result regardless of the host's CUDA install. A
    // dev box with the CUDA toolkit installed would otherwise satisfy every wheel and skip
    // the download/extract/marker path under test. Null = production behavior (real system
    // dirs + ldconfig). Only consulted here; PreloadAll's path resolution is untouched.
    internal Func<string, bool>? SystemLibraryProbeForTests { get; init; }

    private bool IsResolvableOnSystem(string soname)
    {
        // ReSharper disable once InlineTemporaryVariable -- the pattern binding carries the non-null narrowing; inlining it reads worse.
        if (SystemLibraryProbeForTests is { } probe)
            return probe(soname);

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

    private bool LdConfigContains(string soname)
    {
        try
        {
            var supervisor = _processSupervisor?.Invoke();
            if (supervisor is null)
                return false;

            var result = supervisor.RunProbe(
                new ProcessCommand("ldconfig", ["-p"]),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(1),
                    StandardError: ProcessCaptureMode.Discard
                )
            );
            return result.Succeeded
                   && result.StandardOutputText.Contains(
                       soname,
                       StringComparison.Ordinal
                   );
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Deletes the entire shared CUDA cache root (the parent of
    ///     <see cref="CacheDirectory" /> — every bundle version, not just the current
    ///     one) so the next <see cref="EnsureReadyAsync" /> re-downloads from scratch.
    ///     The per-instance gate is layered with a bounded, cross-process maintenance
    ///     lock outside the deleted tree. Maintenance owns root -> every external wheel
    ///     sentinel through the full delete window, so it cannot unlink a live sentinel
    ///     or race another provisioner. When the configured and legacy roots differ,
    ///     clearing also durably disables future adoption of the deliberately-retained
    ///     legacy tree. A missing configured cache is otherwise a no-op (already clear);
    ///     a timeout, marker failure, or delete failure is logged and rethrown so the
    ///     caller can surface it rather than report a corrupt runtime as repaired. Note:
    ///     libraries already dlopen'd this process are held until exit, so a restart is
    ///     required for a fresh re-provision to take effect.
    /// </summary>
    public async Task ClearCacheAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (
                await AcquireMaintenanceLocksAsync(
                        "clearing the CUDA runtime cache",
                        ct
                    )
                    .ConfigureAwait(false)
            )
            {
                if (!PathsEqual(_legacyCacheRoot, _cacheRoot))
                {
                    try
                    {
                        Directory.CreateDirectory(
                            Directory.GetParent(_legacyMigrationDisabledPath)!.FullName
                        );
                        await File.WriteAllTextAsync(
                                _legacyMigrationDisabledPath,
                                string.Empty,
                                ct
                            )
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log?.Invoke(
                            "CUDA runtime: failed to disable legacy cache adoption at "
                                + $"{_legacyMigrationDisabledPath}: {ex.Message} "
                                + "The configured cache was not cleared."
                        );
                        throw;
                    }

                    _legacyMigrationAttempted = true;
                    LogLegacyMigrationDisabled();
                }

                if (!Directory.Exists(_cacheRoot))
                    return;

                Directory.Delete(_cacheRoot, recursive: true);
                _log?.Invoke($"CUDA runtime: cleared cache at {_cacheRoot}.");
            }
        }
        catch (Exception ex)
        {
            // Don't swallow: the caller reports "cleared" to the user only when the
            // cache is actually gone, so a corrupt runtime can't masquerade as repaired.
            _log?.Invoke(
                $"CUDA runtime: failed to clear cache at {_cacheRoot}: {ex.Message}"
            );
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LogLegacyMigrationDisabled()
    {
        var retention = Directory.Exists(_legacyCacheRoot)
            ? $"; the old legacy cache tree at {_legacyCacheRoot} was deliberately retained"
            : "";
        _log?.Invoke(
            $"CUDA runtime: legacy cache adoption is disabled via {_legacyMigrationDisabledPath}{retention}."
        );
    }

    // Remove sibling bundle dirs from earlier BundleVersions so superseded caches
    // don't accumulate (cuDNN alone is ~1.7 GB unpacked).
    // internal so a unit test can assert a different-version sibling dir is deleted while
    // the current version's dir is kept.
    internal void PruneStaleBundles() =>
        PruneStaleBundlesAsync(CancellationToken.None).GetAwaiter().GetResult();

    private async Task PruneStaleBundlesAsync(CancellationToken ct)
    {
        try
        {
            await using (
                await AcquireMaintenanceLocksAsync(
                        "pruning stale CUDA runtime bundles",
                        ct
                    )
                    .ConfigureAwait(false)
            )
            {
                var parent = new DirectoryInfo(_cacheRoot);
                if (!parent.Exists)
                    return;

                foreach (var dir in parent.EnumerateDirectories()
                    .Where(dir =>
                        !string.Equals(dir.Name, BundleVersion, StringComparison.Ordinal)))
                {
                    try { dir.Delete(recursive: true); }
                    catch { /* best effort cleanup */ }
                }
            }
        }
        catch (TimeoutException ex)
        {
            // Cleanup is best-effort; provisioning continues with an explicit reason.
            _log?.Invoke($"CUDA runtime: skipped stale-bundle pruning: {ex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke(
                $"CUDA runtime: skipped stale-bundle pruning because locking failed: {ex.Message}"
            );
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

    // Kept as DllImport: this file is shared-compiled into several plugin projects, so
    // LibraryImport's generated string marshalling would require AllowUnsafeBlocks in every
    // consumer. CharSet.Ansi marshals as UTF-8 on Linux — correct for these libc/libcuda paths.
#pragma warning disable SYSLIB1054, CA2101
    [DllImport("libcuda.so.1", EntryPoint = "cuInit")]
    private static extern int cuInit(uint flags);

    [DllImport("libdl.so.2", CharSet = CharSet.Ansi)]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlerror();
#pragma warning restore SYSLIB1054, CA2101

    private sealed class ExternalLockLease(List<FileStream> locks) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => DisposeLocksAsync(locks);
    }

    private sealed record CudaWheel(
        string Package,
        string Version,
        string[] RequiredSonames
    );

    private sealed record CachePaths(
        string CacheRoot,
        string MaintenanceLockPath,
        string WheelLockDirectory
    );
}
