using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using TypeWhisper.Plugin.SupertonicTts;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SupertonicAssetManagerTests : IDisposable
{
    private const string BaseUrl = "https://example.test/repo";

    private static readonly Dictionary<string, byte[]> s_content = new()
    {
        ["onnx/model.onnx"] = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray(),
        ["voice_styles/M1.json"] = "{\"style\":\"M1\"}"u8.ToArray(),
        ["LICENSE"] = "OpenRAIL-M license text"u8.ToArray(),
    };

    private static readonly IReadOnlyList<SupertonicAssetFile> s_files =
    [
        Pin("onnx/model.onnx", "onnx/model.onnx"),
        Pin("voice_styles/M1.json", "voice_styles/M1.json"),
        Pin(SupertonicPaths.LicenseFileName, "LICENSE"),
    ];

    private readonly string _root = Path.Join(Path.GetTempPath(), "tw-supertonic-" + Guid.NewGuid().ToString("N"));
    private readonly string _assetRoot;

    public SupertonicAssetManagerTests()
    {
        _assetRoot = Path.Join(_root, "Models", SupertonicPaths.ModelDirectoryName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Download_PublishesVerifiedFilesStampsThemAndWritesSource()
    {
        var server = new FakeRepository();
        using var client = new HttpClient(server);
        var sut = Create(client);
        var progress = new List<double>();

        await sut.DownloadMissingAssetsAsync(new SynchronousProgress(progress.Add), CancellationToken.None);

        Assert.True(sut.AreAssetsReady);
        Assert.Equal(s_content["onnx/model.onnx"], await File.ReadAllBytesAsync(AssetPath("onnx/model.onnx")));
        Assert.Equal(s_content["LICENSE"], await File.ReadAllBytesAsync(AssetPath(SupertonicPaths.LicenseFileName)));
        Assert.All(s_files, file => Assert.True(File.Exists(AssetPath(file.RelativePath) + ".verified")));
        Assert.Empty(Directory.EnumerateFiles(_assetRoot, "*.partial", SearchOption.AllDirectories));
        Assert.Contains(SupertonicAssetManager.Revision, await File.ReadAllTextAsync(AssetPath(SupertonicPaths.SourceFileName)));
        Assert.Equal(["/repo/onnx/model.onnx", "/repo/voice_styles/M1.json", "/repo/LICENSE"], server.RequestedPaths);
        Assert.Equal(0, progress[0]);
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task NewSession_TrustsStampedFilesWithoutNetworkOrRehash()
    {
        using (var client = new HttpClient(new FakeRepository()))
            await Create(client).DownloadMissingAssetsAsync(null, CancellationToken.None);

        var offline = new FakeRepository { Fail = true };
        using var offlineClient = new HttpClient(offline);
        var sut = Create(offlineClient);

        Assert.False(sut.AreAssetsReady);
        Assert.True(await sut.VerifyCachedAssetsAsync(CancellationToken.None));
        await sut.DownloadMissingAssetsAsync(null, CancellationToken.None);
        Assert.Empty(offline.RequestedPaths);
    }

    [Fact]
    public async Task CachedFilesFromAnEarlierVersion_AreHashedOnceAndKept()
    {
        // Earlier versions downloaded the same files without stamps.
        foreach (var file in s_files)
            await WriteAssetAsync(file.RelativePath, s_content[file.RemotePath]);

        using var client = new HttpClient(new FakeRepository { Fail = true });
        var sut = Create(client);

        Assert.True(await sut.VerifyCachedAssetsAsync(CancellationToken.None));
        Assert.All(s_files, file => Assert.True(File.Exists(AssetPath(file.RelativePath) + ".verified")));
    }

    [Fact]
    public async Task CorruptOrTruncatedCachedFiles_AreNotReadyAndOnlyThoseAreDownloadedAgain()
    {
        foreach (var file in s_files)
            await WriteAssetAsync(file.RelativePath, s_content[file.RemotePath]);
        var corrupt = s_content["onnx/model.onnx"].ToArray();
        corrupt[100] ^= 0xFF;
        await WriteAssetAsync("onnx/model.onnx", corrupt);
        await WriteAssetAsync("voice_styles/M1.json", s_content["voice_styles/M1.json"][..5]);
        // An abandoned staging file from the earlier non-resumable downloader.
        await WriteAssetAsync("onnx/model.onnx.tmp", [1, 2, 3]);

        var server = new FakeRepository();
        using var client = new HttpClient(server);
        var sut = Create(client);

        Assert.False(await sut.VerifyCachedAssetsAsync(CancellationToken.None));
        await sut.DownloadMissingAssetsAsync(null, CancellationToken.None);

        Assert.True(sut.AreAssetsReady);
        Assert.Equal(["/repo/onnx/model.onnx", "/repo/voice_styles/M1.json"], server.RequestedPaths);
        Assert.Equal(s_content["onnx/model.onnx"], await File.ReadAllBytesAsync(AssetPath("onnx/model.onnx")));
        Assert.False(File.Exists(AssetPath("onnx/model.onnx.tmp")));
    }

    [Fact]
    public async Task FileChangedAfterVerification_IsNoLongerTrusted()
    {
        using var client = new HttpClient(new FakeRepository());
        var sut = Create(client);
        await sut.DownloadMissingAssetsAsync(null, CancellationToken.None);

        var changed = s_content["onnx/model.onnx"].ToArray();
        changed[0] ^= 0xFF;
        await File.WriteAllBytesAsync(AssetPath("onnx/model.onnx"), changed);
        File.SetLastWriteTimeUtc(AssetPath("onnx/model.onnx"), DateTime.UtcNow.AddMinutes(1));

        Assert.False(sut.AreAssetsReady);
        Assert.False(await sut.VerifyCachedAssetsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DownloadWithWrongContent_FailsWithoutPublishingOrKeepingThePartial()
    {
        var server = new FakeRepository { Tamper = "onnx/model.onnx" };
        using var client = new HttpClient(server);
        var sut = Create(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            sut.DownloadMissingAssetsAsync(null, CancellationToken.None));

        Assert.False(sut.AreAssetsReady);
        Assert.False(File.Exists(AssetPath("onnx/model.onnx")));
        Assert.False(File.Exists(AssetPath("onnx/model.onnx") + ".partial"));
        Assert.False(File.Exists(AssetPath("onnx/model.onnx") + ".verified"));
    }

    [Fact]
    public async Task InterruptedDownload_ResumesFromThePartialWithRange()
    {
        var server = new FakeRepository { TruncateOnce = "onnx/model.onnx" };
        using var client = new HttpClient(server);
        var sut = Create(client);

        // The shared downloader is file-linked into several plugins, so match its exception by name.
        var interrupted = await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.DownloadMissingAssetsAsync(null, CancellationToken.None));
        Assert.Equal("DownloadIncompleteException", interrupted.GetType().Name);
        Assert.True(File.Exists(AssetPath("onnx/model.onnx") + ".partial"));

        await sut.DownloadMissingAssetsAsync(null, CancellationToken.None);

        Assert.True(sut.AreAssetsReady);
        Assert.Equal("bytes=2048-", server.RangeHeaders.Last(header => header is not null));
        Assert.Equal(s_content["onnx/model.onnx"], await File.ReadAllBytesAsync(AssetPath("onnx/model.onnx")));
    }

    [Fact]
    public async Task InsufficientSpace_FailsBeforeAnyRequest()
    {
        var server = new FakeRepository();
        using var client = new HttpClient(server);
        var sut = new SupertonicAssetManager(_assetRoot, client, s_files, BaseUrl, _ => 1024);

        await DownloadSpaceAssert.ThrowsInsufficientSpaceAsync(() =>
            sut.DownloadMissingAssetsAsync(null, CancellationToken.None));

        Assert.Empty(server.RequestedPaths);
    }

    [Fact]
    public async Task CancelledDownload_KeepsVerifiedFilesAndThrows()
    {
        var server = new FakeRepository();
        using var client = new HttpClient(server);
        var sut = Create(client);
        await sut.DownloadMissingAssetsAsync(null, CancellationToken.None);
        File.Delete(AssetPath("voice_styles/M1.json"));
        using var cts = new CancellationTokenSource();
        // ReSharper disable once AccessToDisposedClosure -- requests only happen inside the awaited download below, before cts is disposed.
        server.OnRequest = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.DownloadMissingAssetsAsync(null, cts.Token));

        Assert.True(File.Exists(AssetPath("onnx/model.onnx")));
        Assert.False(sut.AreAssetsReady);
    }

    [Fact]
    public async Task Remove_DeletesModelFilesStampsAndPartialsButKeepsUnrelatedFiles()
    {
        using var client = new HttpClient(new FakeRepository());
        var sut = Create(client);
        await sut.DownloadMissingAssetsAsync(null, CancellationToken.None);
        await WriteAssetAsync("voice_styles/M1.json.partial", [1]);
        await WriteAssetAsync("onnx/notes.txt", [1]);

        await sut.RemoveAssetsAsync(CancellationToken.None);

        Assert.False(sut.AreAssetsReady);
        Assert.False(sut.HasAnyAssets);
        Assert.Equal([AssetPath("onnx/notes.txt")], Directory.EnumerateFiles(_assetRoot, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Join(_assetRoot, "voice_styles")));
        Assert.False(await sut.VerifyCachedAssetsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SymlinkedModelFile_IsVerifiedThroughItsTargetAndRemoveKeepsTheTarget()
    {
        var target = Path.Join(_root, "shared", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, s_content["onnx/model.onnx"]);
        Directory.CreateDirectory(Path.Join(_assetRoot, "onnx"));
        File.CreateSymbolicLink(AssetPath("onnx/model.onnx"), target);
        await WriteAssetAsync("voice_styles/M1.json", s_content["voice_styles/M1.json"]);
        await WriteAssetAsync(SupertonicPaths.LicenseFileName, s_content["LICENSE"]);
        using var client = new HttpClient(new FakeRepository { Fail = true });
        var sut = Create(client);

        Assert.True(await sut.VerifyCachedAssetsAsync(CancellationToken.None));
        await sut.RemoveAssetsAsync(CancellationToken.None);

        Assert.False(File.Exists(AssetPath("onnx/model.onnx")));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void DefaultFiles_PinEveryAssetTheSynthesizerLoads()
    {
        var files = SupertonicAssetManager.DefaultFiles;

        Assert.Matches("^[0-9a-f]{40}$", SupertonicAssetManager.Revision);
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the lambda exists to assert on each pinned file.
        Assert.All(files, file =>
        {
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.True(file.SizeBytes > 0);
        });
        Assert.Equal(files.Count, files.Select(file => file.RelativePath).Distinct().Count());
        foreach (var name in new[] { "duration_predictor.onnx", "text_encoder.onnx", "vector_estimator.onnx", "vocoder.onnx", "tts.json", "unicode_indexer.json" })
            Assert.Contains(files, file => file.RelativePath == "onnx/" + name);
        foreach (var voice in new[] { "M1", "M2", "M3", "M4", "M5", "F1", "F2", "F3", "F4", "F5" })
            Assert.Contains(files, file => file.RelativePath == $"voice_styles/{voice}.json");
        Assert.Contains(files, file => file is { RelativePath: SupertonicPaths.LicenseFileName, RemotePath: "LICENSE" });
        Assert.InRange(files.Sum(file => file.SizeBytes), 400_000_000, 402_000_000);
    }

    private SupertonicAssetManager Create(HttpClient client) =>
        new(_assetRoot, client, s_files, BaseUrl, _ => long.MaxValue / 2);

    private string AssetPath(string relativePath) =>
        Path.Join(_assetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task WriteAssetAsync(string relativePath, byte[] content)
    {
        var path = AssetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
    }

    private static SupertonicAssetFile Pin(string relativePath, string remotePath) =>
        new(relativePath, remotePath, s_content[remotePath].Length, Convert.ToHexStringLower(SHA256.HashData(s_content[remotePath])));

    // Progress<T> posts to the thread pool; tests need the reports in order.
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Serves <see cref="s_content" /> under <c>/repo/</c>, honouring Range requests.</summary>
    private sealed class FakeRepository : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public string? Tamper { get; init; }
        public string? TruncateOnce { get; set; }
        public Action<HttpRequestMessage>? OnRequest { get; set; }
        public List<string> RequestedPaths { get; } = [];
        public List<string?> RangeHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            OnRequest?.Invoke(request);
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);
            RangeHeaders.Add(request.Headers.Range?.ToString());
            if (Fail)
                throw new HttpRequestException("offline");

            var remotePath = path["/repo/".Length..];
            var body = s_content[remotePath].ToArray();
            if (remotePath == Tamper)
                body[^1] ^= 0xFF;

            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            var sent = body[(int)from..];
            if (remotePath == TruncateOnce)
            {
                TruncateOnce = null;
                // Declares the whole file but the connection ends halfway.
                response.Content = new ByteArrayContent(sent[..(sent.Length / 2)]);
                response.Content.Headers.ContentLength = sent.Length;
                return Task.FromResult(response);
            }

            response.Content = new ByteArrayContent(sent);
            if (from > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            return Task.FromResult(response);
        }
    }
}
