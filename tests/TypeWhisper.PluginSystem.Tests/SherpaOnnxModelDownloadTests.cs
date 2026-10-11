extern alias SherpaOnnx;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Moq;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxModelDownloadTests : IDisposable
{
    private const string BaseUrl = "https://example.test/repo";
    private const string ModelId = "test-model";

    // Smallest artifacts the structural preflight accepts.
    private static readonly Dictionary<string, byte[]> s_content = new()
    {
        ["encoder.int8.onnx"] = [0x08, 0x09, 0x3a, 0x02, 0x12, 0x00],
        ["tokens.txt"] = "<blk> 0\na 1\nb 2\n"u8.ToArray(),
    };

    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "tw-sherpa-download-" + Guid.NewGuid().ToString("N")
    );

    private string ModelDir => Path.Join(_root, "Models", ModelId);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Catalog_RecommendsUltraAndKeepsTheExistingModels()
    {
        using var plugin = new SherpaOnnxPlugin();
        var models = plugin.TranscriptionModels;

        Assert.Equal(
            ["parakeet-ultra-0.6b", "parakeet-tdt-0.6b", "canary-180m-flash"],
            models.Select(m => m.Id)
        );
        Assert.Equal("parakeet-ultra-0.6b", Assert.Single(models, m => m.IsRecommended).Id);
        Assert.Equal(models[1].LanguageCount, models[0].LanguageCount);
    }

    [Fact]
    public void Catalog_PinsEveryFileToAnImmutableUrlSizeAndHash()
    {
        var catalog = SherpaOnnxPlugin.DefaultModelsForTests;

        Assert.All(
            catalog.SelectMany(m => m.Files),
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- asserting on each file is the test's purpose.
            file =>
            {
                Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
                Assert.True(file.SizeBytes > 0);
                Assert.StartsWith("https://", file.DownloadUrl);
                Assert.DoesNotContain("/resolve/main/", file.DownloadUrl);
                Assert.EndsWith("/" + file.FileName, file.DownloadUrl);
            }
        );
        var ultra = catalog.Single(m => m.Id == "parakeet-ultra-0.6b");
        var tdt = catalog.Single(m => m.Id == "parakeet-tdt-0.6b");
        // Same transducer layout and tokenizer, so the same recognizer and timings apply.
        Assert.Equal(tdt.Files.Select(f => f.FileName), ultra.Files.Select(f => f.FileName));
        Assert.Equal(
            tdt.Files.Single(f => f.FileName == "tokens.txt").Sha256,
            ultra.Files.Single(f => f.FileName == "tokens.txt").Sha256
        );
        Assert.True(ultra.RequiresBlankToken);
        Assert.False(ultra.SupportsTranslation);
    }

    [Fact]
    public async Task Download_PublishesFilesThatMatchTheirPins()
    {
        var server = new FakeRepository();
        using var plugin = Create(server);

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.True(plugin.IsModelDownloaded(ModelId));
        foreach (var (name, bytes) in s_content)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Join(ModelDir, name)));
        Assert.Empty(Directory.GetFiles(ModelDir, "*.partial"));
    }

    [Fact]
    public async Task Download_RejectsAFileThatDoesNotMatchItsHash()
    {
        var server = new FakeRepository { Tamper = "tokens.txt" };
        using var plugin = Create(server);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.DownloadModelAsync(ModelId, null, CancellationToken.None)
        );

        Assert.Contains("tokens.txt", ex.Message);
        Assert.False(plugin.IsModelDownloaded(ModelId));
        Assert.False(File.Exists(Path.Join(ModelDir, "tokens.txt")));
        Assert.False(File.Exists(Path.Join(ModelDir, "tokens.txt.partial")));
        // The verified encoder stays; a retry fetches only what failed.
        Assert.True(File.Exists(Path.Join(ModelDir, "encoder.int8.onnx")));
    }

    [Fact]
    public async Task Download_RejectsAFileOfTheWrongSize()
    {
        using var plugin = Create(new FakeRepository(), sizeOffset: 1);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.DownloadModelAsync(ModelId, null, CancellationToken.None)
        );

        Assert.Contains("encoder.int8.onnx", ex.Message);
        Assert.False(plugin.IsModelDownloaded(ModelId));
    }

    [Fact]
    public async Task Download_ResumesAnInterruptedFileWithRange()
    {
        var server = new FakeRepository { TruncateOnce = "tokens.txt" };
        using var plugin = Create(server);

        // The shared downloader is file-linked into several plugins, so match its exception by name.
        var interrupted = await Assert.ThrowsAnyAsync<Exception>(() =>
            plugin.DownloadModelAsync(ModelId, null, CancellationToken.None)
        );
        Assert.Equal("DownloadIncompleteException", interrupted.GetType().Name);
        Assert.True(File.Exists(Path.Join(ModelDir, "tokens.txt.partial")));

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        var half = s_content["tokens.txt"].Length / 2;
        Assert.Equal($"bytes={half}-", server.RangeHeaders.Last(h => h is not null));
        Assert.Equal(
            s_content["tokens.txt"],
            await File.ReadAllBytesAsync(Path.Join(ModelDir, "tokens.txt"))
        );
    }

    [Fact]
    public async Task CancelledDownload_RemovesItsPartials()
    {
        using var cts = new CancellationTokenSource();
        var server = new FakeRepository { CancelHalfway = ("tokens.txt", cts) };
        using var plugin = Create(server);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.DownloadModelAsync(ModelId, null, cts.Token)
        );

        // Nothing is left that the app could not offer to delete.
        Assert.Empty(Directory.GetFiles(ModelDir, "*.partial"));
        Assert.False(plugin.IsModelDownloaded(ModelId));
    }

    [Fact]
    public async Task Download_KeepsCachedFilesAndRemovesAbandonedUniquePartials()
    {
        Directory.CreateDirectory(ModelDir);
        var cached = Path.Join(ModelDir, "encoder.int8.onnx");
        await File.WriteAllBytesAsync(cached, s_content["encoder.int8.onnx"]);
        var abandoned = Path.Join(ModelDir, $"tokens.txt.{Guid.NewGuid():N}.partial");
        var unrelated = Path.Join(ModelDir, "tokens.txt.backup.partial");
        await File.WriteAllBytesAsync(abandoned, [1, 2, 3]);
        await File.WriteAllBytesAsync(unrelated, [4, 5, 6]);
        var server = new FakeRepository();
        using var plugin = Create(server);

        await plugin.DownloadModelAsync(ModelId, null, CancellationToken.None);

        Assert.Equal(["/repo/tokens.txt"], server.RequestedPaths);
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(unrelated));
        Assert.True(plugin.IsModelDownloaded(ModelId));
    }

    private SherpaOnnxPlugin Create(FakeRepository server, long sizeOffset = 0)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(_root);
        host.Setup(h => h.PluginAssetDirectory).Returns(_root);
        var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(host.Object);
        plugin.SpaceProbe = _ => long.MaxValue;
        plugin.SetDownloadSourceForTests(
            server,
            [
                new SherpaOnnxPlugin.ModelDefinition(
                    ModelId,
                    "Test Model",
                    "~1 MB",
                    1,
                    25,
                    false,
                    false,
                    true,
                    s_content
                        .Select(entry => new SherpaOnnxPlugin.ModelFileDefinition(
                            entry.Key,
                            $"{BaseUrl}/{entry.Key}",
                            entry.Value.Length + sizeOffset,
                            Convert.ToHexStringLower(SHA256.HashData(entry.Value))
                        ))
                        .ToList()
                ),
            ]
        );
        return plugin;
    }

    private sealed class FakeRepository : HttpMessageHandler
    {
        public string? Tamper { get; init; }
        public string? TruncateOnce { get; set; }
        public (string Name, CancellationTokenSource Source)? CancelHalfway { get; init; }
        public List<string> RequestedPaths { get; } = [];
        public List<string?> RangeHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);
            RangeHeaders.Add(request.Headers.Range?.ToString());

            var name = path["/repo/".Length..];
            var body = s_content[name].ToArray();
            if (name == Tamper)
                body[^1] ^= 0xFF;

            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var response = new HttpResponseMessage(
                from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK
            );
            var sent = body[(int)from..];
            if (name == TruncateOnce)
            {
                TruncateOnce = null;
                // Declares the whole file but the connection ends halfway.
                response.Content = new ByteArrayContent(sent[..(sent.Length / 2)]);
                response.Content.Headers.ContentLength = sent.Length;
                return Task.FromResult(response);
            }

            if (CancelHalfway is var (cancelName, source) && cancelName == name)
            {
                response.Content = new StreamContent(new CancellingStream(sent, sent.Length / 2, source));
                response.Content.Headers.ContentLength = sent.Length;
                return Task.FromResult(response);
            }

            response.Content = new ByteArrayContent(sent);
            if (from > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    from,
                    body.Length - 1,
                    body.Length
                );
            return Task.FromResult(response);
        }
    }

    // Serves the first half of the body, then cancels the download as a user would.
    private sealed class CancellingStream(byte[] body, int half, CancellationTokenSource source)
        : MemoryStream(body)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position < half)
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, half - (int)Position)], ct);

            await source.CancelAsync();
            throw new OperationCanceledException(source.Token);
        }
    }
}
