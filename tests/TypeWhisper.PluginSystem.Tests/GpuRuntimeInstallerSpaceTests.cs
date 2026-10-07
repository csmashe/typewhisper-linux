extern alias SherpaOnnx;

using System.Net;
using TypeWhisper.Plugin.WhisperCpp;
using SherpaCudaRuntimeInstaller = SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx.SherpaCudaRuntimeInstaller;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     The GPU runtime archives must fit together with the ~400 MB of libraries
///     extracted beside them; a shortfall fails once the server declares the archive
///     length, before any byte or library is written.
/// </summary>
public sealed class GpuRuntimeInstallerSpaceTests : IDisposable
{
    private const long ArchiveBytes = 4096;
    private const long Extracted = 400L * 1024 * 1024;
    private const long Reserve = 256L * 1024 * 1024;
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "tw-gpu-space-" + Guid.NewGuid().ToString("N")
    );

    public GpuRuntimeInstallerSpaceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task SherpaRuntime_ArchiveFitsButExtractionDoesNot_FailsBeforeWriting()
    {
        using var http = new HttpClient(new ArchiveHandler());
        var installer = new SherpaCudaRuntimeInstaller(_root, http)
        {
            SpaceProbe = _ => ArchiveBytes + Extracted + Reserve - 1,
        };

        var ex = await DownloadSpaceAssert.ThrowsInsufficientSpaceAsync(() =>
            installer.EnsureInstalledAsync(null, CancellationToken.None)
        );

        Assert.StartsWith("Not enough disk space for the sherpa-onnx GPU runtime:", ex.Message);
        Assert.False(installer.IsInstalled);
        Assert.DoesNotContain(
            Directory.GetFiles(_root, "*", SearchOption.AllDirectories),
            path => !path.EndsWith(".lock", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task WhisperRuntime_ArchiveFitsButExtractionDoesNot_FailsBeforeWriting()
    {
        using var http = new HttpClient(new ArchiveHandler());
        var installer = new WhisperCudaRuntimeInstaller(_root, http)
        {
            SpaceProbe = _ => ArchiveBytes + Extracted + Reserve - 1,
        };

        var ex = await DownloadSpaceAssert.ThrowsInsufficientSpaceAsync(() =>
            installer.EnsureInstalledAsync(null, CancellationToken.None)
        );

        Assert.StartsWith("Not enough disk space for the whisper.cpp GPU runtime:", ex.Message);
        Assert.False(installer.IsInstalled);
        Assert.DoesNotContain(
            Directory.GetFiles(_root, "*", SearchOption.AllDirectories),
            path => !path.EndsWith(".lock", StringComparison.Ordinal)
        );
    }

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[ArchiveBytes]),
                }
            );
    }
}
