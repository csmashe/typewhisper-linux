using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Plugins.Shared.Cuda;

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            SystemLibraryProbeForTests = _ => false,
        };

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            SystemLibraryProbeForTests = _ => false,
        };

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            SystemLibraryProbeForTests = _ => false,
        };

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            // Every soname resolvable on the "system" → no wheel is missing.
            SystemLibraryProbeForTests = _ => true,
        };

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http);
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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http);

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            SystemLibraryProbeForTests = _ => false,
        };

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            SystemLibraryProbeForTests = _ => false,
        };

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            SystemLibraryProbeForTests = _ => false,
        };

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
        var provisioner = new CudaRuntimeProvisioner(temp.Path, http)
        {
            SystemLibraryProbeForTests = _ => false,
        };

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

    // ---- fixtures / helpers ------------------------------------------------------------

    private static (FakePyPiHandler Handler, HttpClient Http) WhisperCublasFixture()
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
        var handler = new FakePyPiHandler(fixtures);
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
        private int _json;
        private int _wheel;

        public FakePyPiHandler(IEnumerable<WheelFixture> wheels)
        {
            var list = wheels.ToList();
            _byPackage = list.ToDictionary(w => w.Package, StringComparer.Ordinal);
            _byUrl = list.ToDictionary(w => w.WheelUrl, StringComparer.Ordinal);
        }

        public int JsonRequests => Volatile.Read(ref _json);
        public int WheelRequests => Volatile.Read(ref _wheel);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "pypi.org")
            {
                Interlocked.Increment(ref _json);
                // /pypi/{package}/{version}/json
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var package = parts[1];
                return Task.FromResult(Json(BuildPyPiJson(_byPackage[package])));
            }

            if (!_byUrl.TryGetValue(uri.ToString(), out var fixture))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Interlocked.Increment(ref _wheel);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fixture.Zip),
                });

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
            }
            catch
            {
                // best effort
            }
        }
    }
}
