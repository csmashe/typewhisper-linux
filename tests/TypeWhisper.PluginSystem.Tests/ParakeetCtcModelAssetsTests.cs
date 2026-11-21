using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Moq;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ParakeetCtcModelAssetsTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "ctc-assets-test-" + Guid.NewGuid()
    );
    private string Model => Path.Join(_root, "model");
    private static readonly byte[] s_tokenizer = """
        {"model":{"type":"BPE","vocab":{"a":0},"merges":[]}}
        """u8.ToArray();

    private static byte[] Archive(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var data = new MemoryStream(bytes);
                writer.WriteEntry(
                    new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data }
                );
            }
        using var compressed = new MemoryStream();
        using (
            var compressor = BZip2Stream.Create(
                compressed,
                CompressionMode.Compress,
                false,
                leaveOpen: true
            )
        )
        {
            compressor.Write(output.ToArray());
            // With leaveOpen, disposal does not finalize SharpCompress's encoder.
            compressor.Finish();
        }
        return compressed.ToArray();
    }

    private static byte[] ValidArchive() =>
        Archive(
            ("export/model.int8.onnx", [1, 2, 3]),
            ("export/tokens.txt", "a 0\n<blk> 1024\n"u8.ToArray())
        );

    private static CtcAssetSource Source(string path, byte[] bytes, long maximum = 1024 * 1024) =>
        new(
            "https://fixture.invalid/" + path,
            Convert.ToHexString(SHA256.HashData(bytes)),
            maximum
        );

    private static CtcModelAssets Create(HttpClient client, byte[] archive) =>
        new(client, Source("model", archive), Source("tokenizer", s_tokenizer));

    [Fact]
    public async Task VerifiedBZip2TarAssetsPublishTogetherAndRestartMakesNoRequest()
    {
        var archive = ValidArchive();
        Assert.Equal("BZh", Encoding.ASCII.GetString(archive, 0, 3));
        var requests = 0;
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                {
                    requests++;
                    return Task.FromResult(
                        Response(
                            request.RequestUri!.AbsolutePath == "/model" ? archive : s_tokenizer
                        )
                    );
                }
            )
        );
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(Path.Join(Model, "model.int8.onnx"))
        );
        Assert.Equal(1024, new CtcTokenizer(Path.Join(Model, "tokens.txt")).BlankId);
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
    }

    [Fact]
    public async Task HashFailureLeavesPriorDirectoryUntouchedAndCanRetry()
    {
        Directory.CreateDirectory(Model);
        var prior = Path.Join(Model, "keep-existing.txt");
        await File.WriteAllTextAsync(prior, "previous assets");
        var archive = ValidArchive();
        var corrupt = true;
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                    Task.FromResult(
                        Response(
                            request.RequestUri!.AbsolutePath == "/tokenizer" ? s_tokenizer
                            // ReSharper disable once AccessToModifiedClosure -- the handler reads the flag per request; the test clears it between the failing call and the retry.
                            : corrupt ? [9]
                            : archive
                        )
                    )
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(client, archive).EnsureAsync(Model, CancellationToken.None)
        );
        Assert.Equal("previous assets", await File.ReadAllTextAsync(prior));
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
        corrupt = false;
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.True(File.Exists(Path.Join(Model, "verified-assets.json")));
        var backup = Assert.Single(Directory.GetDirectories(_root, "model.previous-*"));
        Assert.Equal(
            "previous assets",
            await File.ReadAllTextAsync(Path.Join(backup, "keep-existing.txt"))
        );
    }

    [Fact]
    public async Task CancellationDuringHttpDrainsWithoutPublishingPartialAssets()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(
            new Handler(
                async (_, ct) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return Response([]);
                }
            )
        );
        var run = Create(client, ValidArchive()).EnsureAsync(Model, cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(Directory.Exists(Model));
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
    }

    [Theory]
    [InlineData("../model.int8.onnx")]
    [InlineData("/model.int8.onnx")]
    public async Task TraversalArchiveIsRejectedBeforePublishing(string entry)
    {
        var archive = Archive(
            (entry, [1]),
            ("tokens.txt", "a 0\n<blk> 1024\n"u8.ToArray())
        );
        using var client = new HttpClient(
            new Handler((_, _) => Task.FromResult(Response(archive)))
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(client, archive).EnsureAsync(Model, CancellationToken.None)
        );
        Assert.False(Directory.Exists(Model));
        Assert.False(File.Exists(Path.Join(_root, "model.int8.onnx")));
    }

    [Fact]
    public async Task ResponseSizeLimitRejectsOtherwiseMatchingHash()
    {
        var archive = ValidArchive();
        using var client = new HttpClient(
            new Handler((_, _) => Task.FromResult(Response(archive)))
        );
        var installer = new CtcModelAssets(
            client,
            Source("model", archive, 1),
            Source("tokenizer", s_tokenizer)
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.EnsureAsync(Model, CancellationToken.None)
        );
        Assert.False(Directory.Exists(Model));
    }

    [Fact]
    public async Task OversizedStreamStopsNearTheLimitInsteadOfAtEndOfStream()
    {
        var served = new EndlessStream();
        using var client = new HttpClient(
            new Handler(
                (_, _) =>
                    Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StreamContent(served),
                        }
                    )
            )
        );
        const long maximum = 256 * 1024;
        var installer = new CtcModelAssets(
            client,
            Source("model", [], maximum),
            Source("tokenizer", s_tokenizer)
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.EnsureAsync(Model, CancellationToken.None)
        );
        Assert.InRange(served.BytesRead, maximum, maximum + 1024 * 1024);
        Assert.False(Directory.Exists(Model));
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
    }

    [Fact]
    public async Task ChangedInstalledBytesRequireVerifiedRepair()
    {
        var archive = ValidArchive();
        var requests = 0;
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                {
                    requests++;
                    return Task.FromResult(
                        Response(
                            request.RequestUri!.AbsolutePath == "/model" ? archive : s_tokenizer
                        )
                    );
                }
            )
        );
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        File.WriteAllBytes(Path.Join(Model, "model.int8.onnx"), [9]);
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(4, requests);
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(Path.Join(Model, "model.int8.onnx"))
        );
    }

    [Fact]
    public async Task UnchangedVerifiedAssetsAreNotHashedAgainUntilTheirStampChanges()
    {
        var archive = ValidArchive();
        var requests = 0;
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                {
                    requests++;
                    return Task.FromResult(
                        Response(
                            request.RequestUri!.AbsolutePath == "/model" ? archive : s_tokenizer
                        )
                    );
                }
            )
        );
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        // Files written moments ago get no stamp: a rewrite within the file-time resolution could keep it.
        Assert.Empty(Stamps());
        AgeInstalledFiles();
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(["model.int8.onnx"], Stamps().Keys);
        var receipt = Path.Join(Model, "verified-assets.json");
        var receiptWritten = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(receipt, receiptWritten);
        var model = Path.Join(Model, "model.int8.onnx");
        var modelWritten = File.GetLastWriteTimeUtc(model);
        // Different bytes behind the recorded size and last write time: a matching stamp means the file is not read again.
        await File.WriteAllBytesAsync(model, [3, 2, 1]);
        File.SetLastWriteTimeUtc(model, modelWritten);
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(new byte[] { 3, 2, 1 }, await File.ReadAllBytesAsync(model));
        Assert.Equal(receiptWritten, File.GetLastWriteTimeUtc(receipt));
        // The small files are hashed every time, so their corruption is repaired despite a kept write time.
        var tokens = Path.Join(Model, "tokens.txt");
        var tokensWritten = File.GetLastWriteTimeUtc(tokens);
        var tokensBytes = await File.ReadAllBytesAsync(tokens);
        await File.WriteAllBytesAsync(tokens, tokensBytes.Select(b => (byte)(b ^ 1)).ToArray());
        File.SetLastWriteTimeUtc(tokens, tokensWritten);
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(4, requests);
        Assert.Equal(tokensBytes, await File.ReadAllBytesAsync(tokens));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(model));
        // A changed model stamp is verified by hashing again, and the mismatch is repaired.
        AgeInstalledFiles();
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        modelWritten = File.GetLastWriteTimeUtc(model);
        await File.WriteAllBytesAsync(model, [3, 2, 1]);
        File.SetLastWriteTimeUtc(model, modelWritten.AddSeconds(2));
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(6, requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(model));
    }

    [Fact]
    public async Task ReceiptWithoutStampsIsVerifiedOnceAndUpgraded()
    {
        var archive = ValidArchive();
        var requests = 0;
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                {
                    requests++;
                    return Task.FromResult(
                        Response(
                            request.RequestUri!.AbsolutePath == "/model" ? archive : s_tokenizer
                        )
                    );
                }
            )
        );
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        var receipt = Path.Join(Model, "verified-assets.json");
        // The receipt format before stamps existed: the pinned source hashes and one hash per file.
        var legacy = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!.AsObject();
        legacy.Remove("Stamps");
        await File.WriteAllTextAsync(receipt, legacy.ToJsonString());
        AgeInstalledFiles();
        await Create(client, archive).EnsureAsync(Model, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(["model.int8.onnx"], Stamps().Keys);
        var upgraded = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!;
        foreach (var field in new[] { "ArchiveHash", "TokenizerHash", "Files" })
            Assert.Equal(legacy[field]!.ToJsonString(), upgraded[field]!.ToJsonString());
        Assert.Empty(Directory.GetFiles(_root, ".ctc-receipt-*"));
    }

    [Fact]
    public async Task ModelThatFailsToLoadIsHashedAndRepairedOnTheNextActivation()
    {
        var archive = ValidArchive();
        var requests = 0;
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                {
                    requests++;
                    return Task.FromResult(
                        Response(
                            request.RequestUri!.AbsolutePath == "/model" ? archive : s_tokenizer
                        )
                    );
                }
            )
        );
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.GetSetting<string>("ModelDirectory")).Returns((string?)null);
        host.SetupGet(h => h.PluginAssetDirectory).Returns(_root);
        var model = Path.Join(Model, "model.int8.onnx");
        using var plugin = new ParakeetCtcPlugin(
            client,
            modelFactory: _ => throw new InvalidDataException("corrupt model"),
            assetsFactory: http => Create(http, archive)
        );
        await plugin.ActivateAsync(host.Object);
        await plugin.DeactivateAsync();
        AgeInstalledFiles();
        await plugin.ActivateAsync(host.Object);
        Assert.Equal(["model.int8.onnx"], Stamps().Keys);
        await plugin.DeactivateAsync();
        // Same-size corruption behind the recorded write time passes the stamp check.
        var written = File.GetLastWriteTimeUtc(model);
        await File.WriteAllBytesAsync(model, [3, 2, 1]);
        File.SetLastWriteTimeUtc(model, written);
        await plugin.ActivateAsync(host.Object);
        Assert.Equal(2, requests);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.RescoreAsync(
                new VocabularyRescoreRequest(
                    Guid.NewGuid(),
                    "a",
                    ReadOnlyMemory<float>.Empty,
                    16000,
                    [],
                    []
                ),
                CancellationToken.None
            )
        );
        Assert.Empty(Stamps());
        await plugin.ActivateAsync(host.Object);
        Assert.Equal(4, requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(model));
    }

    // Moves the installed files' last write times back, as they are on every launch after the one that installed them.
    private void AgeInstalledFiles()
    {
        foreach (var path in Directory.GetFiles(Model))
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(-10));
    }

    private Dictionary<string, JsonNode?> Stamps() =>
        JsonNode
            .Parse(File.ReadAllText(Path.Join(Model, "verified-assets.json")))!["Stamps"]
            ?.AsObject()
            .ToDictionary(entry => entry.Key, entry => entry.Value)
        ?? [];

    [Fact]
    public async Task InsufficientSpaceFailsBeforeExtractionAndKeepsPriorAssets()
    {
        Directory.CreateDirectory(Model);
        var prior = Path.Join(Model, "keep-existing.txt");
        await File.WriteAllTextAsync(prior, "previous assets");
        var archive = ValidArchive();
        var requests = new List<string>();
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                {
                    requests.Add(request.RequestUri!.AbsolutePath);
                    return Task.FromResult(
                        Response(request.RequestUri.AbsolutePath == "/model" ? archive : s_tokenizer)
                    );
                }
            )
        );
        // Room for the archive itself but not for the extraction staged beside it.
        var assets = new CtcModelAssets(
            client,
            Source("model", archive),
            Source("tokenizer", s_tokenizer),
            _ => archive.Length + 256L * 1024 * 1024
        );

        var ex = await DownloadSpaceAssert.ThrowsInsufficientSpaceAsync(() =>
            assets.EnsureAsync(Model, CancellationToken.None)
        );

        Assert.StartsWith("Not enough disk space for the dictionary boosting model:", ex.Message);
        Assert.Equal(["/model"], requests);
        Assert.Equal("previous assets", await File.ReadAllTextAsync(prior));
        Assert.Empty(Directory.GetDirectories(_root, ".ctc-staging-*"));
        Assert.Empty(Directory.GetDirectories(_root, "model.previous-*"));
    }

    [Fact]
    public async Task UnknownFreeSpaceStillInstalls()
    {
        var archive = ValidArchive();
        using var client = new HttpClient(
            new Handler(
                (request, _) =>
                    Task.FromResult(
                        Response(request.RequestUri!.AbsolutePath == "/model" ? archive : s_tokenizer)
                    )
            )
        );

        await new CtcModelAssets(
            client,
            Source("model", archive),
            Source("tokenizer", s_tokenizer),
            _ => null
        ).EnsureAsync(Model, CancellationToken.None);

        Assert.True(File.Exists(Path.Join(Model, "verified-assets.json")));
    }

    private static HttpResponseMessage Response(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            BytesRead += count;
            return count;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => handler(request, cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
