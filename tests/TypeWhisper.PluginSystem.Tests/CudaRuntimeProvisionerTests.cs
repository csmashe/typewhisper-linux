using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Plugins.Shared.Cuda;
using TypeWhisper.Plugins.Shared.Io;

namespace TypeWhisper.PluginSystem.Tests;

// Pure unit tests for the shared CUDA provisioner's network+disk half. They drive the
// internal DownloadAndExtractAsync / ExtractSharedObjects / PruneStaleBundles against a
// fake HttpMessageHandler (canned PyPI JSON + in-memory wheel zips) and a temp cache dir,
// never touching dlopen, the driver probe, or a real GPU.
//
// The on-system library probe is overridden (SystemLibraryProbeForTests) so results are
// deterministic regardless of the host's CUDA install: this dev box ships the full CUDA
// 12 toolkit, so the real probe would mark every wheel satisfied and skip the download
// path entirely.
//
// EXPLICITLY OMITTED (would need real native libs + a model, so it can't run in CI):
// "provisioning fails → CPU FromPath/recognizer SUCCEEDS". That whole-stack success path
// is covered by the H4 manual GPU validation plan and by normal usage; here we only
// assert the provisioning state machines up to (not through) a real native load.
public class CudaRuntimeProvisionerTests
{
    // These must match CudaRuntimeProvisioner's private wheel definitions for the
    // WhisperCublas profile (kept in lockstep by the version tests elsewhere).
    private const string CudartPackage = "nvidia-cuda-runtime-cu12";
    private const string CudartVersion = "12.9.79";
    private const string CublasPackage = "nvidia-cublas-cu12";
    private const string CublasVersion = "12.9.2.10";

    [Fact]
    public async Task DownloadAndExtract_ColdCache_DownloadsExtractsAndWritesMarkers()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None);

        // One metadata + one wheel request per missing wheel (cudart + cublas).
        Assert.Equal(2, handler.JsonRequests);
        Assert.Equal(2, handler.WheelRequests);

        // Every required soname flattened into the cache root.
        Assert.True(File.Exists(Path.Join(provisioner.CacheDirectory, "libcudart.so.12")));
        Assert.True(File.Exists(Path.Join(provisioner.CacheDirectory, "libcublas.so.12")));
        Assert.True(File.Exists(Path.Join(provisioner.CacheDirectory, "libcublasLt.so.12")));

        // Completion markers written (so a warm cache short-circuits the next run).
        Assert.True(File.Exists(
            Path.Join(provisioner.CacheDirectory, $".{CudartPackage}-{CudartVersion}.complete")));
        Assert.True(File.Exists(
            Path.Join(provisioner.CacheDirectory, $".{CublasPackage}-{CublasVersion}.complete")));

        // No staging files left behind.
        Assert.Empty(Directory.GetFiles(provisioner.CacheDirectory, "*.tmp"));

        // And the profile now reports satisfied (marker + cached sonames).
        Assert.True(provisioner.IsProfileSatisfied(CudaRuntimeProfile.WhisperCublas));
    }

    [Fact]
    public async Task DownloadAndExtract_WarmCache_IsSatisfied_MakesNoSecondRequest()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None);
        Assert.Equal(2, handler.JsonRequests);
        Assert.Equal(2, handler.WheelRequests);

        // Second call: markers + cached sonames satisfy every wheel → no new requests.
        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None);
        Assert.Equal(2, handler.JsonRequests);
        Assert.Equal(2, handler.WheelRequests);
    }

    [Fact]
    public async Task DownloadAndExtract_MarkerDeleted_ReDownloadsThatWheelOnly()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None);

        // Drop the cudart marker: the primary soname is still on disk, but without the
        // marker the cache must be treated as unsatisfied and the wheel re-fetched.
        File.Delete(Path.Join(provisioner.CacheDirectory, $".{CudartPackage}-{CudartVersion}.complete"));

        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None);

        // Exactly one extra metadata + wheel request (cudart only; cublas stayed satisfied).
        Assert.Equal(3, handler.JsonRequests);
        Assert.Equal(3, handler.WheelRequests);
    }

    [Fact]
    public async Task DownloadAndExtract_WhenSystemProvidesLibraries_DownloadsNothing()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            // Every soname resolvable on the "system" → no wheel is missing.
            systemLibraryProbe: _ => true
        );

        var progress = new RecordingProgress();
        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, progress, CancellationToken.None);

        Assert.Equal(0, handler.JsonRequests);
        Assert.Equal(0, handler.WheelRequests);
        Assert.Equal(1.0, progress.Last);
    }

    [Fact]
    public void ExtractSharedObjects_KeepsOnlyLibSharedObjects_FlattenedNoTmp()
    {
        using var temp = new TempDir();
        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(temp.Path, http);
        Directory.CreateDirectory(provisioner.CacheDirectory);

        var zip = BuildWheelZip(
            ("nvidia/cuda_runtime/lib/libcudart.so.12", 16),   // kept
            ("nvidia/cuda_runtime/lib/libextra.so", 16),       // kept (.so)
            ("nvidia/cuda_runtime/lib/__init__.py", 16),       // ignored (not a .so)
            ("nvidia/cuda_runtime/include/cuda.so", 16),       // ignored (not under /lib/)
            ("nvidia/cuda_runtime/lib/", 0));                  // ignored (directory entry)
        var wheelPath = Path.Join(temp.Path, "wheel.whl");
        File.WriteAllBytes(wheelPath, zip);

        provisioner.ExtractSharedObjects(wheelPath);

        Assert.True(File.Exists(Path.Join(provisioner.CacheDirectory, "libcudart.so.12")));
        Assert.True(File.Exists(Path.Join(provisioner.CacheDirectory, "libextra.so")));
        Assert.False(File.Exists(Path.Join(provisioner.CacheDirectory, "__init__.py")));
        Assert.False(File.Exists(Path.Join(provisioner.CacheDirectory, "cuda.so")));
        Assert.Empty(Directory.GetFiles(provisioner.CacheDirectory, "*.tmp"));
    }

    [Fact]
    public void PruneStaleBundles_DeletesOtherVersions_KeepsCurrent()
    {
        using var temp = new TempDir();
        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(temp.Path, http);

        // CacheDirectory = <temp>/<BundleVersion>; create it plus a stale sibling.
        Directory.CreateDirectory(provisioner.CacheDirectory);
        var staleDir = Path.Join(temp.Path, "cuda12-v0-stale");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Join(staleDir, "old.so"), "x");

        provisioner.PruneStaleBundles();

        Assert.False(Directory.Exists(staleDir));
        Assert.True(Directory.Exists(provisioner.CacheDirectory));
    }

    [Fact]
    public async Task DownloadAndExtract_FailsClosed_WhenPyPiOmitsSha256()
    {
        using var temp = new TempDir();
        var fixtures = new[]
        {
            Wheel(CudartPackage, CudartVersion, ("nvidia/cuda_runtime/lib/libcudart.so.12", 16),
                nullSha: true),
            Wheel(CublasPackage, CublasVersion,
            [
                ("nvidia/cublas/lib/libcublas.so.12", 16),
                    ("nvidia/cublas/lib/libcublasLt.so.12", 16),
            ], nullSha: true),
        };
        var handler = new FakePyPiHandler(fixtures);
        using var http = new HttpClient(handler);
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.DownloadAndExtractAsync(
                CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None));

        // Nothing unverified cached: no .so files, no markers.
        Assert.Empty(Directory.GetFiles(provisioner.CacheDirectory, "*.so*"));
        Assert.Empty(Directory.GetFiles(provisioner.CacheDirectory, ".*.complete"));
    }

    [Fact]
    public async Task DownloadAndExtract_FailsClosed_WhenNoManylinuxWheel()
    {
        using var temp = new TempDir();
        var fixtures = new[]
        {
            Wheel(CudartPackage, CudartVersion, ("nvidia/cuda_runtime/lib/libcudart.so.12", 16),
                noManylinux: true),
            Wheel(CublasPackage, CublasVersion,
            [
                ("nvidia/cublas/lib/libcublas.so.12", 16),
                    ("nvidia/cublas/lib/libcublasLt.so.12", 16),
            ], noManylinux: true),
        };
        var handler = new FakePyPiHandler(fixtures);
        using var http = new HttpClient(handler);
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.DownloadAndExtractAsync(
                CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(provisioner.CacheDirectory, "*.so*"));
    }

    [Fact]
    public async Task DownloadAndExtract_ProgressAdvancesByActualBytes_WhenSizeOmitted()
    {
        using var temp = new TempDir();
        // First wheel omits its metadata size (so the cumulative counter must advance by
        // the actual bytes read — the L5 fix); second wheel reports a (larger) real size,
        // and both are big enough to read in multiple chunks. Without the fix the
        // cumulative progress would reset toward zero at the second wheel, so a
        // non-decreasing assertion catches the regression.
        var cudart = Wheel(
            CudartPackage, CudartVersion,
            ("nvidia/cuda_runtime/lib/libcudart.so.12", 200_000),
            omitSize: true);
        var cublas = Wheel(
            CublasPackage, CublasVersion,
            [
                ("nvidia/cublas/lib/libcublas.so.12", 200_000),
                ("nvidia/cublas/lib/libcublasLt.so.12", 100_000),
            ]);
        var handler = new FakePyPiHandler([cudart, cublas]);
        using var http = new HttpClient(handler);
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        var progress = new RecordingProgress();
        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, progress, CancellationToken.None);

        Assert.NotEmpty(progress.Values);
        // Monotonic non-decreasing and never above 1.0.
        for (var i = 1; i < progress.Values.Count; i++)
            Assert.True(
                progress.Values[i] >= progress.Values[i - 1] - 1e-9,
                $"progress decreased: {progress.Values[i - 1]} -> {progress.Values[i]}");
        Assert.All(progress.Values, v => Assert.InRange(v, 0.0, 1.0));
        // A real intermediate step occurred before completion (not a bare 0→1 jump).
        Assert.Contains(progress.Values, v => v is > 0.0 and < 1.0);
        Assert.Equal(1.0, progress.Last);
    }

    [Fact]
    public async Task DownloadAndExtract_TwoConcurrentCalls_GateSerializes_SingleDownload()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        var a = provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None);
        var b = provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas, null, CancellationToken.None);
        await Task.WhenAll(a, b);

        // The _gate serializes the two calls: the first downloads each wheel once, the
        // second finds them satisfied. Each wheel is fetched exactly once, not twice.
        Assert.Equal(2, handler.JsonRequests);
        Assert.Equal(2, handler.WheelRequests);
    }

    [Fact]
    public async Task MissingRecomputedUnderLocks_ClearDuringMetadataResolution_Refetches()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture(pauseFirstMetadataResponse: true);
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );
        var clearingProvisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );
        SeedCachedWheel(
            provisioner,
            CublasPackage,
            CublasVersion,
            "libcublas.so.12",
            "libcublasLt.so.12"
        );

        var profileSatisfiedUnderLocks = false;
        var provisioning = provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            () =>
            {
                profileSatisfiedUnderLocks = provisioner.IsProfileSatisfied(
                    CudaRuntimeProfile.WhisperCublas
                );
                return Task.CompletedTask;
            },
            CancellationToken.None
        );
        await handler.FirstMetadataResponseStarted.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await clearingProvisioner
                .ClearCacheAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Directory.Exists(temp.Path));
        }
        finally
        {
            handler.ReleaseFirstMetadataResponse();
        }

        await provisioning.WaitAsync(TimeSpan.FromSeconds(5));

        // Only cudart was missing in the initial snapshot. Clear removed the warm
        // cuBLAS wheel, so it needed fresh metadata and a fetch after recomputation.
        Assert.Equal(2, handler.JsonRequests);
        Assert.Equal(2, handler.WheelRequests);
        Assert.True(profileSatisfiedUnderLocks);
        Assert.True(provisioner.IsProfileSatisfied(CudaRuntimeProfile.WhisperCublas));
    }

    [Fact]
    public async Task AlreadySatisfied_ClearWaitsThroughPreload()
    {
        using var temp = new TempDir();
        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );
        var clearingProvisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );
        SeedCachedWheel(
            provisioner,
            CudartPackage,
            CudartVersion,
            "libcudart.so.12"
        );
        SeedCachedWheel(
            provisioner,
            CublasPackage,
            CublasVersion,
            "libcublas.so.12",
            "libcublasLt.so.12"
        );

        var preloadStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releasePreload = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var provisioning = provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            async () =>
            {
                preloadStarted.TrySetResult(true);
                await releasePreload.Task.WaitAsync(TimeSpan.FromSeconds(5));
            },
            CancellationToken.None
        );
        await preloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var clearing = clearingProvisioner.ClearCacheAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => clearing.WaitAsync(TimeSpan.FromMilliseconds(250))
            );
            Assert.True(Directory.Exists(provisioner.CacheDirectory));
            Assert.True(
                File.Exists(Path.Join(provisioner.CacheDirectory, "libcudart.so.12"))
            );
        }
        finally
        {
            releasePreload.TrySetResult(true);
        }

        await provisioning.WaitAsync(TimeSpan.FromSeconds(5));
        await clearing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(temp.Path));
    }

    [Fact]
    public async Task DownloadAndExtract_TwoProvisioners_ClearWaitsForActiveProvisioning()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture(pauseFirstWheelResponse: true);
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );
        var clearingProvisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );

        var provisioning = provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            CancellationToken.None
        );
        await handler.FirstWheelRequestStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var cudartSentinel = Path.Join(
            provisioner.WheelLockDirectoryForTests,
            CudartPackage + ".lock"
        );
        Assert.True(File.Exists(cudartSentinel));

        var clearing = clearingProvisioner.ClearCacheAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => clearing.WaitAsync(TimeSpan.FromMilliseconds(500))
            );
        }
        finally
        {
            handler.ReleaseFirstWheelResponse();
        }

        await provisioning;
        await clearing;

        Assert.False(Directory.Exists(temp.Path));
        Assert.True(File.Exists(provisioner.MaintenanceLockPathForTests));
        Assert.True(File.Exists(cudartSentinel));
    }

    [Fact]
    public async Task PruneStaleBundles_TwoProvisioners_WaitsForActiveProvisioning()
    {
        using var temp = new TempDir();
        var (handler, http) = WhisperCublasFixture(pauseFirstWheelResponse: true);
        using var _ = http;
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );
        var pruningProvisioner = CreateProvisioner(
            temp.Path,
            http,
            systemLibraryProbe: _ => false
        );
        var staleDir = Path.Join(temp.Path, "cuda12-v0-stale");
        Directory.CreateDirectory(staleDir);
        await File.WriteAllTextAsync(Path.Join(staleDir, "old.so"), "x");

        var provisioning = provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            CancellationToken.None
        );
        await handler.FirstWheelRequestStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var pruning = Task.Run(pruningProvisioner.PruneStaleBundles);
        bool staleSurvivedWhileProvisioning;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => pruning.WaitAsync(TimeSpan.FromMilliseconds(500))
            );
            staleSurvivedWhileProvisioning = Directory.Exists(staleDir);
        }
        finally
        {
            handler.ReleaseFirstWheelResponse();
        }

        await provisioning;
        await pruning;

        Assert.True(staleSurvivedWhileProvisioning);
        Assert.False(Directory.Exists(staleDir));
    }

    [Fact]
    public async Task PruneStaleBundles_WhenMaintenanceLockTimesOut_SkipsWithReason()
    {
        using var temp = new TempDir();
        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var logs = new List<string>();
        var provisioner = CreateProvisioner(
            temp.Path,
            http,
            logs.Add,
            maintenanceLockTimeout: TimeSpan.FromMilliseconds(100)
        );
        Directory.CreateDirectory(provisioner.CacheDirectory);
        var staleDir = Path.Join(temp.Path, "cuda12-v0-stale");
        Directory.CreateDirectory(staleDir);
        await File.WriteAllTextAsync(Path.Join(staleDir, "old.so"), "x");
        await using var heldMaintenanceLock = new FileStream(
            provisioner.MaintenanceLockPathForTests,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );

        provisioner.PruneStaleBundles();

        Assert.True(Directory.Exists(staleDir));
        Assert.Contains(
            logs,
            message =>
                message.Contains(
                    "skipped stale-bundle pruning: Timed out waiting",
                    StringComparison.Ordinal
                )
        );
    }

    [Fact]
    public void CacheRootForPluginAssetDirectory_NoDirectory_UsesLegacyDefault()
    {
        Assert.Equal(
            CudaRuntimeProvisioner.DefaultCacheRoot(),
            CudaRuntimeProvisioner.CacheRootForPluginAssetDirectory(null)
        );
    }

    [Fact]
    public async Task ClearCache_AfterFailedLegacyMigration_NewInstanceDownloadsFresh()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var legacyArtifact = Path.Join(
            legacyRoot,
            CudaRuntimeProvisioner.BundleVersion,
            "legacy-artifact.so"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(legacyArtifact)!);
        await File.WriteAllTextAsync(legacyArtifact, "legacy");

        var (handler, http) = WhisperCublasFixture();
        using var _ = http;
        var failedMigrator = CreateProvisioner(
            configuredRoot,
            http,
            systemLibraryProbe: _ => true,
            legacyCacheRoot: legacyRoot,
            moveDirectory: (_, _) =>
                throw new IOException("simulated cross-device move failure")
        );

        await failedMigrator
            .DownloadAndExtractAsync(
                CudaRuntimeProfile.WhisperCublas,
                null,
                CancellationToken.None
            )
            .WaitAsync(TimeSpan.FromSeconds(5));
        await failedMigrator
            .ClearCacheAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            Path.Join(temp.Path, "legacy", "cuda.legacy-migration-disabled"),
            failedMigrator.LegacyMigrationDisabledPathForTests
        );
        Assert.True(File.Exists(failedMigrator.LegacyMigrationDisabledPathForTests));
        Assert.False(Directory.Exists(configuredRoot));

        var restartedProvisioner = CreateProvisioner(
            configuredRoot,
            http,
            systemLibraryProbe: _ => false,
            legacyCacheRoot: legacyRoot
        );
        await restartedProvisioner
            .DownloadAndExtractAsync(
                CudaRuntimeProfile.WhisperCublas,
                null,
                CancellationToken.None
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, handler.JsonRequests);
        Assert.Equal(2, handler.WheelRequests);
        Assert.True(File.Exists(legacyArtifact));
        Assert.False(
            File.Exists(Path.Join(restartedProvisioner.CacheDirectory, "legacy-artifact.so"))
        );
        Assert.True(
            restartedProvisioner.IsProfileSatisfied(CudaRuntimeProfile.WhisperCublas)
        );
    }

    [Fact]
    public async Task ClearCache_MissingConfiguredRoot_StillDisablesLegacyMigration()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var legacyArtifact = Path.Join(
            legacyRoot,
            CudaRuntimeProvisioner.BundleVersion,
            "legacy-artifact.so"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(legacyArtifact)!);
        await File.WriteAllTextAsync(legacyArtifact, "legacy");

        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var clearingProvisioner = CreateProvisioner(
            configuredRoot,
            http,
            legacyCacheRoot: legacyRoot
        );

        await clearingProvisioner
            .ClearCacheAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(Directory.Exists(configuredRoot));
        Assert.True(File.Exists(clearingProvisioner.LegacyMigrationDisabledPathForTests));

        var moveAttempts = 0;
        var restartedProvisioner = CreateProvisioner(
            configuredRoot,
            http,
            systemLibraryProbe: _ => true,
            legacyCacheRoot: legacyRoot,
            moveDirectory: (_, _) => moveAttempts++
        );
        await restartedProvisioner
            .DownloadAndExtractAsync(
                CudaRuntimeProfile.WhisperCublas,
                null,
                CancellationToken.None
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, moveAttempts);
        Assert.True(File.Exists(legacyArtifact));
        Assert.False(
            File.Exists(Path.Join(restartedProvisioner.CacheDirectory, "legacy-artifact.so"))
        );
    }

    [Fact]
    public async Task ClearCache_TombstoneCreationFails_DoesNotDeleteConfiguredCache()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var logs = new List<string>();
        var provisioner = CreateProvisioner(
            configuredRoot,
            http,
            logs.Add,
            legacyCacheRoot: legacyRoot
        );
        var configuredArtifact = Path.Join(
            provisioner.CacheDirectory,
            "configured-artifact.so"
        );
        Directory.CreateDirectory(provisioner.CacheDirectory);
        await File.WriteAllTextAsync(configuredArtifact, "configured");
        Directory.CreateDirectory(provisioner.LegacyMigrationDisabledPathForTests);

        var exception = await Record.ExceptionAsync(
            () =>
                provisioner
                    .ClearCacheAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5))
        );

        Assert.NotNull(exception);
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Unexpected exception type: {exception.GetType().FullName}"
        );
        Assert.True(File.Exists(configuredArtifact));
        Assert.Contains(
            logs,
            message =>
                message.Contains(
                    "failed to disable legacy cache adoption",
                    StringComparison.Ordinal
                )
                && message.Contains(
                    "configured cache was not cleared",
                    StringComparison.OrdinalIgnoreCase
                )
        );
    }

    // PA105: real crash durability (fsync reaching the platter) can't be exercised in a
    // unit test — these three pin the mechanism instead: the tombstone goes through the
    // staged -> file-fsync -> rename -> parent-dir-fsync sequence, and a failure at either
    // sync point aborts the Clear before the configured cache is deleted.
    [Fact]
    public async Task ClearCache_TombstoneWrite_StagesFsyncsRenamesThenFsyncsParent()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var tombstonePath = Path.Join(temp.Path, "legacy", "cuda.legacy-migration-disabled");
        var (_, http) = WhisperCublasFixture();
        using var _ = http;

        var events = new List<string>();
        var provisioner = CreateProvisioner(
            configuredRoot,
            http,
            legacyCacheRoot: legacyRoot,
            tombstoneSyncHooks: new DurableFileWrite.SyncHooks(
                // ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local -- asserting on the hook arguments IS this test's purpose.
                (tempPath, stream) =>
                {
                    // The staged sibling is fsynced writable, before publication.
                    Assert.True(stream.CanWrite);
                    Assert.StartsWith(tombstonePath + ".", tempPath, StringComparison.Ordinal);
                    Assert.EndsWith(".tmp", tempPath, StringComparison.Ordinal);
                    Assert.False(File.Exists(tombstonePath));
                    events.Add("sync-file");
                },
                // ReSharper restore ParameterOnlyUsedForPreconditionCheck.Local
                directoryPath =>
                {
                    // The parent-dir fsync runs after the rename made the marker visible.
                    Assert.Equal(Path.GetDirectoryName(tombstonePath), directoryPath);
                    Assert.True(File.Exists(tombstonePath));
                    events.Add("sync-directory");
                }
            )
        );
        Directory.CreateDirectory(provisioner.CacheDirectory);

        await provisioner
            .ClearCacheAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "sync-file", "sync-directory" }, events);
        Assert.Equal(tombstonePath, provisioner.LegacyMigrationDisabledPathForTests);
        Assert.True(File.Exists(tombstonePath));
        Assert.False(Directory.Exists(configuredRoot));
        Assert.Empty(
            Directory.GetFiles(Path.GetDirectoryName(tombstonePath)!, "*.tmp")
        );
    }

    [Fact]
    public async Task ClearCache_TombstoneParentFsyncFails_DoesNotDeleteConfiguredCache()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var logs = new List<string>();
        var provisioner = CreateProvisioner(
            configuredRoot,
            http,
            logs.Add,
            legacyCacheRoot: legacyRoot,
            tombstoneSyncHooks: new DurableFileWrite.SyncHooks(
                (_, _) => { },
                _ => throw new IOException("simulated parent directory fsync failure")
            )
        );
        var configuredArtifact = Path.Join(
            provisioner.CacheDirectory,
            "configured-artifact.so"
        );
        Directory.CreateDirectory(provisioner.CacheDirectory);
        await File.WriteAllTextAsync(configuredArtifact, "configured");

        var exception = await Assert.ThrowsAsync<IOException>(
            () =>
                provisioner
                    .ClearCacheAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5))
        );

        // The tombstone's durability is unknown, so the Clear must abort with the
        // configured cache intact rather than risk re-arming legacy re-adoption.
        Assert.Contains("parent directory fsync failure", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(configuredArtifact));
        Assert.Contains(
            logs,
            message =>
                message.Contains(
                    "failed to disable legacy cache adoption",
                    StringComparison.Ordinal
                )
                && message.Contains(
                    "configured cache was not cleared",
                    StringComparison.OrdinalIgnoreCase
                )
        );
    }

    [Fact]
    public async Task ClearCache_TombstoneFileFsyncFails_LeavesNoMarkerOrStagingBehind()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var tombstonePath = Path.Join(temp.Path, "legacy", "cuda.legacy-migration-disabled");
        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var provisioner = CreateProvisioner(
            configuredRoot,
            http,
            legacyCacheRoot: legacyRoot,
            tombstoneSyncHooks: new DurableFileWrite.SyncHooks(
                (_, _) => throw new IOException("simulated staged file fsync failure"),
                _ => { }
            )
        );
        var configuredArtifact = Path.Join(
            provisioner.CacheDirectory,
            "configured-artifact.so"
        );
        Directory.CreateDirectory(provisioner.CacheDirectory);
        await File.WriteAllTextAsync(configuredArtifact, "configured");

        await Assert.ThrowsAsync<IOException>(
            () =>
                provisioner
                    .ClearCacheAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5))
        );

        // An unsynced marker is never published, its staging sibling is cleaned up, and
        // the configured cache survives.
        Assert.False(File.Exists(tombstonePath));
        Assert.Empty(
            Directory.GetFiles(Path.GetDirectoryName(tombstonePath)!, "*.tmp")
        );
        Assert.True(File.Exists(configuredArtifact));
    }

    [Fact]
    public async Task LegacyMigration_WaitingBehindClear_ObservesTombstoneUnderLease()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var legacyArtifact = Path.Join(
            legacyRoot,
            CudaRuntimeProvisioner.BundleVersion,
            "legacy-artifact.so"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(legacyArtifact)!);
        await File.WriteAllTextAsync(legacyArtifact, "legacy");

        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var warmProvisioner = CreateProvisioner(
            configuredRoot,
            http,
            systemLibraryProbe: _ => false,
            legacyCacheRoot: legacyRoot
        );
        SeedCachedWheel(
            warmProvisioner,
            CudartPackage,
            CudartVersion,
            "libcudart.so.12"
        );
        SeedCachedWheel(
            warmProvisioner,
            CublasPackage,
            CublasVersion,
            "libcublas.so.12",
            "libcublasLt.so.12"
        );

        var leaseHeld = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseLease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var warming = warmProvisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            async () =>
            {
                leaseHeld.TrySetResult(true);
                await releaseLease.Task.WaitAsync(TimeSpan.FromSeconds(5));
            },
            CancellationToken.None
        );
        await leaseHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Directory.Delete(configuredRoot, recursive: true);

        var clearingProvisioner = CreateProvisioner(
            configuredRoot,
            http,
            legacyCacheRoot: legacyRoot
        );
        var moveAttempts = 0;
        var migratingProvisioner = CreateProvisioner(
            configuredRoot,
            http,
            systemLibraryProbe: _ => true,
            legacyCacheRoot: legacyRoot,
            moveDirectory: (_, _) => moveAttempts++
        );
        var clearing = clearingProvisioner.ClearCacheAsync(CancellationToken.None);
        var migrating = Task.CompletedTask;

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => clearing.WaitAsync(TimeSpan.FromMilliseconds(250))
            );
            Assert.False(
                File.Exists(clearingProvisioner.LegacyMigrationDisabledPathForTests)
            );

            migrating = migratingProvisioner.DownloadAndExtractAsync(
                CudaRuntimeProfile.WhisperCublas,
                null,
                CancellationToken.None
            );
            await Assert.ThrowsAsync<TimeoutException>(
                () => migrating.WaitAsync(TimeSpan.FromMilliseconds(250))
            );
        }
        finally
        {
            releaseLease.TrySetResult(true);
            await warming.WaitAsync(TimeSpan.FromSeconds(5));
            await clearing.WaitAsync(TimeSpan.FromSeconds(5));
            await migrating.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(File.Exists(clearingProvisioner.LegacyMigrationDisabledPathForTests));
        Assert.Equal(0, moveAttempts);
        Assert.True(File.Exists(legacyArtifact));
        Assert.False(
            File.Exists(Path.Join(migratingProvisioner.CacheDirectory, "legacy-artifact.so"))
        );
    }

    [Fact]
    public async Task DownloadAndExtract_LegacyCacheAndMissingConfiguredRoot_MovesCacheAtomically()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var legacyBundle = Path.Join(
            legacyRoot,
            CudaRuntimeProvisioner.BundleVersion
        );
        Directory.CreateDirectory(legacyBundle);
        await File.WriteAllTextAsync(
            Path.Join(legacyBundle, "migrated-artifact.so"),
            "legacy"
        );

        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var logs = new List<string>();
        var provisioner = CreateProvisioner(
            configuredRoot,
            http,
            logs.Add,
            systemLibraryProbe: _ => true,
            legacyCacheRoot: legacyRoot
        );

        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            CancellationToken.None
        );

        Assert.False(Directory.Exists(legacyRoot));
        Assert.Equal(
            "legacy",
            await File.ReadAllTextAsync(
                Path.Join(provisioner.CacheDirectory, "migrated-artifact.so")
            )
        );
        Assert.Contains(
            logs,
            message => message.Contains("migrated cache", StringComparison.Ordinal)
        );

        // Sentinels remain external to the moved cache. The old set stays in place,
        // while future provisioning uses the independently-created new set.
        var legacyCacheParent = Directory.GetParent(legacyRoot)!;
        Assert.True(
            File.Exists(
                Path.Join(
                    legacyCacheParent.FullName,
                    Path.GetFileName(legacyRoot) + ".maintenance.lock"
                )
            )
        );
        Assert.True(
            Directory.Exists(
                Path.Join(
                    legacyCacheParent.FullName,
                    Path.GetFileName(legacyRoot) + ".locks"
                )
            )
        );
        Assert.True(File.Exists(provisioner.MaintenanceLockPathForTests));
        Assert.True(Directory.Exists(provisioner.WheelLockDirectoryForTests));
    }

    [Fact]
    public async Task DownloadAndExtract_LegacyMoveFails_LeavesOldCacheAndProvisionsConfiguredRoot()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var legacyArtifact = Path.Join(
            legacyRoot,
            CudaRuntimeProvisioner.BundleVersion,
            "legacy-artifact.so"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(legacyArtifact)!);
        await File.WriteAllTextAsync(legacyArtifact, "legacy");

        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var logs = new List<string>();
        var moveAttempts = 0;
        var provisioner = CreateProvisioner(
            configuredRoot,
            http,
            logs.Add,
            systemLibraryProbe: _ => true,
            legacyCacheRoot: legacyRoot,
            moveDirectory: (_, _) =>
            {
                moveAttempts++;
                throw new IOException("simulated cross-device move failure");
            }
        );

        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            CancellationToken.None
        );

        Assert.Equal(1, moveAttempts);
        Assert.True(File.Exists(legacyArtifact));
        Assert.True(Directory.Exists(provisioner.CacheDirectory));
        Assert.False(
            File.Exists(Path.Join(provisioner.CacheDirectory, "legacy-artifact.so"))
        );
        Assert.Contains(
            logs,
            message =>
                message.Contains("could not migrate cache", StringComparison.Ordinal)
                && message.Contains(
                    "Leaving the old cache in place",
                    StringComparison.Ordinal
                )
        );
    }

    [Fact]
    public async Task DownloadAndExtract_LegacyMaintenanceLockHeld_BoundsMigrationAndProvisionsConfiguredRoot()
    {
        using var temp = new TempDir();
        var legacyRoot = Path.Join(temp.Path, "legacy", "cuda");
        var configuredRoot = Path.Join(temp.Path, "selected", "Runtimes", "cuda");
        var legacyArtifact = Path.Join(
            legacyRoot,
            CudaRuntimeProvisioner.BundleVersion,
            "legacy-artifact.so"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(legacyArtifact)!);
        await File.WriteAllTextAsync(legacyArtifact, "legacy");

        var legacyCacheParent = Directory.GetParent(legacyRoot)!;
        var legacyMaintenanceLockPath = Path.Join(
            legacyCacheParent.FullName,
            Path.GetFileName(legacyRoot) + ".maintenance.lock"
        );
        await using var heldMaintenanceLock = new FileStream(
            legacyMaintenanceLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );

        var (_, http) = WhisperCublasFixture();
        using var _ = http;
        var logs = new List<string>();
        var provisioner = CreateProvisioner(
            configuredRoot,
            http,
            logs.Add,
            systemLibraryProbe: _ => true,
            legacyCacheRoot: legacyRoot,
            maintenanceLockTimeout: TimeSpan.FromMilliseconds(100)
        );

        var stopwatch = Stopwatch.StartNew();
        await provisioner.DownloadAndExtractAsync(
            CudaRuntimeProfile.WhisperCublas,
            null,
            CancellationToken.None
        );
        stopwatch.Stop();

        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(75),
            TimeSpan.FromSeconds(5)
        );
        Assert.True(File.Exists(legacyArtifact));
        Assert.True(Directory.Exists(provisioner.CacheDirectory));
        Assert.Contains(
            logs,
            message =>
                message.Contains(
                    "Timed out waiting for another CUDA cache operation",
                    StringComparison.Ordinal
                )
        );
    }

    // ---- fixtures / helpers ------------------------------------------------------------

    private static void SeedCachedWheel(
        CudaRuntimeProvisioner provisioner,
        string package,
        string version,
        params string[] sonames
    )
    {
        Directory.CreateDirectory(provisioner.CacheDirectory);
        foreach (var soname in sonames)
            File.WriteAllText(Path.Join(provisioner.CacheDirectory, soname), "cached");
        File.WriteAllText(
            Path.Join(provisioner.CacheDirectory, $".{package}-{version}.complete"),
            version
        );
    }

    private static CudaRuntimeProvisioner CreateProvisioner(
        string cacheRoot,
        HttpClient http,
        Action<string>? log = null,
        Func<string, bool>? systemLibraryProbe = null,
        string? legacyCacheRoot = null,
        Action<string, string>? moveDirectory = null,
        TimeSpan? maintenanceLockTimeout = null,
        DurableFileWrite.SyncHooks? tombstoneSyncHooks = null
    ) =>
        new(
            cacheRoot,
            http,
            log,
            // Keep every existing unit test isolated from the real per-user default.
            legacyCacheRoot ?? cacheRoot + ".test-legacy-cuda",
            moveDirectory ?? Directory.Move
        )
        {
            SystemLibraryProbeForTests = systemLibraryProbe,
            MaintenanceLockTimeoutForTests =
                maintenanceLockTimeout ?? TimeSpan.FromSeconds(30),
            TombstoneSyncHooksForTests = tombstoneSyncHooks,
        };

    private static (FakePyPiHandler Handler, HttpClient Http) WhisperCublasFixture(
        bool pauseFirstWheelResponse = false,
        bool pauseFirstMetadataResponse = false
    )
    {
        var fixtures = new[]
        {
            Wheel(CudartPackage, CudartVersion, ("nvidia/cuda_runtime/lib/libcudart.so.12", 16)),
            Wheel(CublasPackage, CublasVersion,
            [
                ("nvidia/cublas/lib/libcublas.so.12", 16),
                ("nvidia/cublas/lib/libcublasLt.so.12", 16),
            ]),
        };
        var handler = new FakePyPiHandler(
            fixtures,
            pauseFirstWheelResponse,
            pauseFirstMetadataResponse
        );
        return (handler, new HttpClient(handler));
    }

    private static WheelFixture Wheel(
        string package,
        string version,
        (string Path, int Bytes) entry,
        bool omitSize = false,
        bool nullSha = false,
        bool noManylinux = false) =>
        Wheel(package, version, [entry], omitSize, nullSha, noManylinux);

    private static WheelFixture Wheel(
        string package,
        string version,
        (string Path, int Bytes)[] entries,
        bool omitSize = false,
        bool nullSha = false,
        bool noManylinux = false) =>
        new()
        {
            Package = package,
            Version = version,
            Zip = BuildWheelZip(entries),
            OmitSize = omitSize,
            NullSha = nullSha,
            NoManylinux = noManylinux,
        };

    private static byte[] BuildWheelZip(params (string Path, int Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                // Directory entries (trailing slash) carry no content.
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                if (bytes <= 0)
                    continue;
                using var s = entry.Open();
                var buf = new byte[bytes];
                for (var i = 0; i < bytes; i++)
                    buf[i] = (byte)(i % 251);
                s.Write(buf, 0, buf.Length);
            }
        }

        return ms.ToArray();
    }

    private sealed class WheelFixture
    {
        public required string Package { get; init; }
        public required string Version { get; init; }
        public required byte[] Zip { get; init; }
        public bool OmitSize { get; init; }
        public bool NullSha { get; init; }
        public bool NoManylinux { get; init; }

        public string WheelUrl => $"https://files.example.test/{Package}/{Version}.whl";

        public string Sha256 =>
            Convert.ToHexString(SHA256.HashData(Zip)).ToLowerInvariant();

        public string Filename =>
            NoManylinux
                ? $"{Package}-{Version}-py3-none-win_amd64.whl"
                : $"{Package}-{Version}-py3-none-manylinux2014_x86_64.whl";
    }

    private sealed class FakePyPiHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, WheelFixture> _byPackage;
        private readonly Dictionary<string, WheelFixture> _byUrl;
        private readonly bool _pauseFirstWheelResponse;
        private readonly bool _pauseFirstMetadataResponse;
        private readonly TaskCompletionSource<bool> _firstWheelRequestStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstWheelResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstMetadataResponseStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstMetadataResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _json;
        private int _wheel;

        public FakePyPiHandler(
            IEnumerable<WheelFixture> wheels,
            bool pauseFirstWheelResponse = false,
            bool pauseFirstMetadataResponse = false
        )
        {
            var list = wheels.ToList();
            _byPackage = list.ToDictionary(w => w.Package, StringComparer.Ordinal);
            _byUrl = list.ToDictionary(w => w.WheelUrl, StringComparer.Ordinal);
            _pauseFirstWheelResponse = pauseFirstWheelResponse;
            _pauseFirstMetadataResponse = pauseFirstMetadataResponse;
        }

        public int JsonRequests => Volatile.Read(ref _json);
        public int WheelRequests => Volatile.Read(ref _wheel);
        public Task FirstWheelRequestStarted => _firstWheelRequestStarted.Task;
        public Task FirstMetadataResponseStarted => _firstMetadataResponseStarted.Task;

        public void ReleaseFirstWheelResponse() =>
            _releaseFirstWheelResponse.TrySetResult(true);

        public void ReleaseFirstMetadataResponse() =>
            _releaseFirstMetadataResponse.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var uri = request.RequestUri!;
            if (uri.Host == "pypi.org")
            {
                var metadataRequest = Interlocked.Increment(ref _json);
                if (_pauseFirstMetadataResponse && metadataRequest == 1)
                {
                    _firstMetadataResponseStarted.TrySetResult(true);
                    await _releaseFirstMetadataResponse.Task.WaitAsync(
                        TimeSpan.FromSeconds(5),
                        cancellationToken
                    );
                }

                // /pypi/{package}/{version}/json
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var package = parts[1];
                return Json(BuildPyPiJson(_byPackage[package]));
            }

            if (!_byUrl.TryGetValue(uri.ToString(), out var fixture))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var wheelRequest = Interlocked.Increment(ref _wheel);
            // ReSharper disable once InvertIf -- the positive form states the pause case this stub injects.
            if (_pauseFirstWheelResponse && wheelRequest == 1)
            {
                _firstWheelRequestStarted.TrySetResult(true);
                await _releaseFirstWheelResponse.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    cancellationToken
                );
            }

            return
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fixture.Zip),
                };
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

        private static string BuildPyPiJson(WheelFixture w)
        {
            var size = w.OmitSize ? string.Empty : $"\"size\": {w.Zip.Length},";
            var sha = w.NullSha ? "\"sha256\": null" : $"\"sha256\": \"{w.Sha256}\"";
            return $$"""
                {
                  "urls": [
                    {
                      "packagetype": "bdist_wheel",
                      "filename": "{{w.Filename}}",
                      "url": "{{w.WheelUrl}}",
                      {{size}}
                      "digests": { {{sha}} }
                    }
                  ]
                }
                """;
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        private readonly Lock _sync = new();
        public List<double> Values { get; } = [];
        public double Last
        {
            get
            {
                lock (_sync)
                    return Values.Count == 0 ? double.NaN : Values[^1];
            }
        }

        public void Report(double value)
        {
            lock (_sync)
                Values.Add(value);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            "tw-cuda-prov-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);

                var parent = Directory.GetParent(Path);
                if (parent is null)
                    return;

                var cacheName = System.IO.Path.GetFileName(Path);
                var lockDirectory = System.IO.Path.Join(
                    parent.FullName,
                    cacheName + ".locks"
                );
                if (Directory.Exists(lockDirectory))
                    Directory.Delete(lockDirectory, recursive: true);

                var maintenanceLock = System.IO.Path.Join(
                    parent.FullName,
                    cacheName + ".maintenance.lock"
                );
                if (File.Exists(maintenanceLock))
                    File.Delete(maintenanceLock);

                var legacyMigrationMarker = System.IO.Path.Join(
                    parent.FullName,
                    cacheName + ".test-legacy-cuda.legacy-migration-disabled"
                );
                if (File.Exists(legacyMigrationMarker))
                    File.Delete(legacyMigrationMarker);
            }
            catch
            {
                // best effort
            }
        }
    }
}
