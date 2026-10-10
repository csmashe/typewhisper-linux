using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Moq;
using TypeWhisper.Plugin.GemmaLocal;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Gemma model downloads resume through the shared downloader and only publish a file
///     that matches the pinned hash; cached files are checked against every accepted build.
///     A tiny injected catalog stands in for the multi-gigabyte models.
/// </summary>
public sealed class GemmaLocalModelDownloadTests : IDisposable
{
    private const string ModelId = "test-model";
    private const string FileName = "test-model.gguf";
    private const string Url = "https://models.test/pinned/test-model.gguf";

    private static readonly byte[] s_pinnedBytes = Bytes(4096, seed: 1);
    private static readonly byte[] s_earlierBytes = Bytes(3000, seed: 2);
    private static readonly GemmaModelDefinition s_model = new(
        ModelId,
        "Test Model",
        "~4 KB",
        1,
        true,
        Url,
        FileName,
        new GemmaModelFile(s_pinnedBytes.Length, Sha256(s_pinnedBytes)),
        [new GemmaModelFile(s_earlierBytes.Length, Sha256(s_earlierBytes))]);

    private readonly string _root = Path.Join(Path.GetTempPath(), "tw-gemma-" + Guid.NewGuid().ToString("N"));
    private readonly string _modelDir;
    private readonly string _modelPath;

    public GemmaLocalModelDownloadTests()
    {
        _modelDir = Path.Join(_root, "Models", ModelId);
        _modelPath = Path.Join(_modelDir, FileName);
        Directory.CreateDirectory(_modelDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Download_FreshModel_PublishesVerifiedFileFromPinnedUrl()
    {
        var handler = new RangeHandler(s_pinnedBytes);
        using var plugin = await CreateAsync(handler);

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Equal(s_pinnedBytes, await File.ReadAllBytesAsync(_modelPath));
        Assert.Equal([Url], handler.RequestedUrls);
        Assert.True(plugin.IsModelVerified(ModelId));
        Assert.False(File.Exists(_modelPath + ".partial"));
    }

    [Fact]
    public async Task Download_SurvivingPartial_ResumesWithRange()
    {
        await File.WriteAllBytesAsync(_modelPath + ".partial", s_pinnedBytes[..1000]);
        var handler = new RangeHandler(s_pinnedBytes);
        using var plugin = await CreateAsync(handler);

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Equal(["bytes=1000-"], handler.ReceivedRanges);
        Assert.Equal(s_pinnedBytes, await File.ReadAllBytesAsync(_modelPath));
        Assert.True(plugin.IsModelVerified(ModelId));
    }

    [Fact]
    public async Task Download_ServerIgnoresRange_RestartsFromZero()
    {
        // A stale prefix that the server will not extend must not be kept.
        await File.WriteAllBytesAsync(_modelPath + ".partial", Bytes(1000, seed: 9));
        var handler = new RangeHandler(s_pinnedBytes) { HonorRange = false };
        using var plugin = await CreateAsync(handler);

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Equal(s_pinnedBytes, await File.ReadAllBytesAsync(_modelPath));
    }

    [Fact]
    public async Task Download_DroppedConnection_KeepsPartialForTheNextAttempt()
    {
        var handler = new RangeHandler(s_pinnedBytes) { DropAfterBytes = 1500 };
        using var plugin = await CreateAsync(handler);

        await Assert.ThrowsAsync<IOException>(
            () => plugin.DownloadModelAsync(ModelId, null, CancellationToken.None));
        Assert.Equal(1500, new FileInfo(_modelPath + ".partial").Length);
        Assert.False(File.Exists(_modelPath));

        handler.DropAfterBytes = null;
        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Equal([null, "bytes=1500-"], handler.ReceivedRanges);
        Assert.Equal(s_pinnedBytes, await File.ReadAllBytesAsync(_modelPath));
    }

    [Fact]
    public async Task Download_HashMismatch_DiscardsPartialAndTheUnverifiedCache()
    {
        // A cached earlier-size file whose bytes match no build: removed up front, never restored.
        var previous = Bytes(3000, seed: 7);
        await File.WriteAllBytesAsync(_modelPath, previous);
        var tampered = (byte[])s_pinnedBytes.Clone();
        tampered[^1] ^= 0xFF;
        using var plugin = await CreateAsync(new RangeHandler(tampered));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => plugin.DownloadModelAsync(ModelId, null, CancellationToken.None));

        Assert.Contains("failed its integrity check", ex.Message);
        Assert.False(File.Exists(_modelPath));
        Assert.False(File.Exists(_modelPath + ".partial"));
        Assert.False(plugin.IsModelDownloaded(ModelId));
    }

    [Fact]
    public async Task Download_UnexpectedLength_IsRejected()
    {
        using var plugin = await CreateAsync(new RangeHandler(s_pinnedBytes[..4000]));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => plugin.DownloadModelAsync(ModelId, null, CancellationToken.None));

        Assert.Contains("has 4000 bytes; expected 4096", ex.Message);
        Assert.False(File.Exists(_modelPath));
    }

    [Fact]
    public async Task Download_CachedEarlierBuild_IsAcceptedWithoutARequest()
    {
        await File.WriteAllBytesAsync(_modelPath, s_earlierBytes);
        var handler = new RangeHandler(s_pinnedBytes);
        using var plugin = await CreateAsync(handler);

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Empty(handler.RequestedUrls);
        Assert.Equal(s_earlierBytes, await File.ReadAllBytesAsync(_modelPath));
        Assert.True(plugin.IsModelVerified(ModelId));
    }

    [Fact]
    public async Task Download_CorruptCachedFile_IsReplacedByThePinnedBuild()
    {
        var corrupt = (byte[])s_pinnedBytes.Clone();
        corrupt[0] ^= 0xFF;
        await File.WriteAllBytesAsync(_modelPath, corrupt);
        var handler = new RangeHandler(s_pinnedBytes);
        using var plugin = await CreateAsync(handler);

        Assert.True(plugin.IsModelDownloaded(ModelId));
        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Single(handler.RequestedUrls);
        Assert.Equal(s_pinnedBytes, await File.ReadAllBytesAsync(_modelPath));
        Assert.True(plugin.IsModelVerified(ModelId));
    }

    [Fact]
    public async Task Download_CorruptCachedFile_IsRemovedBeforeTheSpaceCheck()
    {
        var corrupt = (byte[])s_pinnedBytes.Clone();
        corrupt[0] ^= 0xFF;
        await File.WriteAllBytesAsync(_modelPath, corrupt);
        using var plugin = await CreateAsync(new RangeHandler(s_pinnedBytes));
        // Room for one copy only: the preflight fails unless the corrupt file is already gone.
        plugin.SpaceProbe = _ => File.Exists(_modelPath) ? 0 : long.MaxValue;

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Equal(s_pinnedBytes, await File.ReadAllBytesAsync(_modelPath));
        Assert.True(plugin.IsModelVerified(ModelId));
    }

    [Fact]
    public async Task Download_RemovesAbandonedTempFilesFromEarlierVersions()
    {
        var abandoned = Path.Join(_modelDir, $"{FileName}.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Join(_modelDir, $"{FileName}.notes.tmp");
        await File.WriteAllBytesAsync(abandoned, [1, 2, 3]);
        await File.WriteAllBytesAsync(unrelated, [1]);
        using var plugin = await CreateAsync(new RangeHandler(s_pinnedBytes));

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Verification_FileChangedAfterStamp_IsNoLongerTrusted()
    {
        using var plugin = await CreateAsync(new RangeHandler(s_pinnedBytes));
        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);
        Assert.True(plugin.IsModelVerified(ModelId));

        File.SetLastWriteTimeUtc(_modelPath, DateTime.UtcNow.AddMinutes(5));

        Assert.False(plugin.IsModelVerified(ModelId));
        Assert.True(plugin.IsModelDownloaded(ModelId));
    }

    [Fact]
    public async Task Verification_StampNamingAnUnacceptedHash_IsNotTrusted()
    {
        await File.WriteAllBytesAsync(_modelPath, s_pinnedBytes);
        var file = new FileInfo(_modelPath);
        GemmaModelVerification.WriteStamp(_modelPath, new string('0', 64), file);
        using var plugin = await CreateAsync(new RangeHandler(s_pinnedBytes));

        Assert.False(plugin.IsModelVerified(ModelId));
    }

    [Fact]
    public void Verification_ConcurrentStampWriters_DoNotCollide()
    {
        File.WriteAllBytes(_modelPath, s_pinnedBytes);
        var file = new FileInfo(_modelPath);

        Parallel.For(0, 64, _ => GemmaModelVerification.WriteStamp(_modelPath, s_model.Pinned.Sha256, file));

        Assert.True(GemmaModelVerification.IsVerified(s_model, _modelPath));
        Assert.Empty(Directory.EnumerateFiles(_modelDir, "*.tmp"));
    }

    [Fact]
    public async Task Download_SymlinkedCachedModel_IsVerifiedThroughItsTarget()
    {
        var target = Path.Join(_root, "elsewhere.gguf");
        await File.WriteAllBytesAsync(target, s_pinnedBytes);
        File.CreateSymbolicLink(_modelPath, target);
        var handler = new RangeHandler(s_pinnedBytes);
        using var plugin = await CreateAsync(handler);

        Assert.True(plugin.IsModelDownloaded(ModelId));
        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Empty(handler.RequestedUrls);
        Assert.True(plugin.IsModelVerified(ModelId));
    }

    [Fact]
    public async Task Load_CorruptCachedFile_FailsIntegrityBeforeNativeLoad()
    {
        var corrupt = (byte[])s_pinnedBytes.Clone();
        corrupt[100] ^= 0xFF;
        await File.WriteAllBytesAsync(_modelPath, corrupt);
        using var plugin = await CreateAsync(new RangeHandler(s_pinnedBytes));
        plugin.SelectModel(ModelId);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => plugin.LoadModelAsync(ModelId, CancellationToken.None));

        Assert.Contains("Select the model again to re-download it", ex.Message);
        Assert.Null(plugin.LoadedModelId);
        Assert.False(File.Exists(GemmaModelVerification.StampPath(_modelPath)));
    }

    [Fact]
    public async Task IsModelDownloaded_RequiresAnAcceptedSize()
    {
        using var plugin = await CreateAsync(new RangeHandler(s_pinnedBytes));
        await File.WriteAllBytesAsync(_modelPath, Bytes(10, seed: 3));

        Assert.False(plugin.IsModelDownloaded(ModelId));
    }

    private async Task<GemmaLocalPlugin> CreateAsync(HttpMessageHandler handler)
    {
        var plugin = new GemmaLocalPlugin(
            null,
            GemmaLocalPlugin.EnsureRequestedModelIsActive,
            [s_model],
            handler)
        {
            SpaceProbe = _ => long.MaxValue,
        };
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(_root);
        host.Setup(h => h.PluginAssetDirectory).Returns(_root);
        await plugin.ActivateAsync(host.Object);
        return plugin;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Serves one body with Range semantics and can drop the connection mid-body.</summary>
    private sealed class RangeHandler(byte[] body) : HttpMessageHandler
    {
        public bool HonorRange { get; init; } = true;
        public long? DropAfterBytes { get; set; }
        public List<string> RequestedUrls { get; } = [];
        public List<string?> ReceivedRanges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            var range = request.Headers.Range;
            ReceivedRanges.Add(range?.ToString());

            var from = HonorRange ? range?.Ranges.Single().From ?? 0 : 0;
            if (from >= body.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            var slice = body[(int)from..];
            Stream stream = DropAfterBytes is { } drop
                ? new DroppingStream(slice, (int)Math.Max(0, drop - from))
                : new MemoryStream(slice, writable: false);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = slice.Length;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = content,
            };
            if (from > 0)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class DroppingStream(byte[] data, int serveBytes) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= serveBytes)
                throw new IOException("Connection reset.");

            var read = Math.Min(count, serveBytes - _position);
            Array.Copy(data, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
