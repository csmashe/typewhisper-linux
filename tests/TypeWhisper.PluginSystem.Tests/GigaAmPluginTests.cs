extern alias GigaAm;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GigaAm::TypeWhisper.Plugin.GigaAm;
using Moq;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using InsufficientDownloadSpaceException = GigaAm::TypeWhisper.Plugins.Shared.Net.InsufficientDownloadSpaceException;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GigaAmPluginTests : IDisposable
{
    private const int SampleRate = 16000;
    private const string FixtureUrl = "https://fixture.invalid/model";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "gigaam-tests-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void VersionMatchesTheManifest()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(SourceManifestPath()),
            s_jsonOptions
        )!;
        using var plugin = new GigaAmPlugin();

        Assert.Equal(manifest.Id, plugin.PluginId);
        Assert.Equal(manifest.Version, plugin.PluginVersion);
    }

    [Fact]
    public void OffersOneRussianModelWithoutTranslation()
    {
        using var plugin = new GigaAmPlugin();

        Assert.Equal("gigaam", plugin.ProviderId);
        Assert.Equal(["ru"], plugin.SupportedLanguages);
        Assert.False(plugin.SupportsTranslation);
        Assert.True(plugin.SupportsModelDownload);
        var model = Assert.Single(plugin.TranscriptionModels);
        Assert.Equal("gigaam-v3-e2e-rnnt", model.Id);
        Assert.Equal(1, model.LanguageCount);
        Assert.Equal(216, model.EstimatedSizeMB);
        Assert.False(model.SupportsTranslation);
    }

    [Fact]
    public void ModelFilesArePinnedToOneRevision()
    {
        Assert.Matches("^[0-9a-f]{40}$", GigaAmModelAssets.Revision);
        Assert.Equal(
            "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/" + GigaAmModelAssets.Revision,
            GigaAmModelAssets.DefaultBaseUrl
        );
        Assert.Equal(
            [
                GigaAmModelFiles.Encoder,
                GigaAmModelFiles.Decoder,
                GigaAmModelFiles.Joint,
                GigaAmModelFiles.Vocabulary,
            ],
            GigaAmModelAssets.DefaultFiles.Select(file => file.Name)
        );
        Assert.All(
            GigaAmModelAssets.DefaultFiles,
            file => Assert.Matches("^[0-9a-f]{64}$", file.Sha256)
        );
    }

    [Fact]
    public void PreprocessorShipsWithThePlugin()
    {
        // onnx-asr 0.12.0, onnx_asr/preprocessors/data/gigaam_v3.onnx, as named in THIRD-PARTY-NOTICES.md.
        Assert.Equal(
            "919C3BC8A434B6E733E5FE5DCD247974D0A7B1F2BC5B6995B6AD55F7225E014D",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(GigaAmPlugin.PreprocessorPath)))
        );
    }

    [Fact]
    public async Task DownloadPublishesVerifiedFilesAndSkipsThemNextTime()
    {
        var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out _);
        var host = Host();
        await plugin.ActivateAsync(host.Object);
        var reports = new List<double>();

        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        await plugin.DownloadModelAsync(
            GigaAmPlugin.ModelId,
            new SyncProgress(reports.Add),
            CancellationToken.None
        );

        Assert.True(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        Assert.Equal(4, server.Requests.Count);
        Assert.Equal(1, reports[^1], 3);
        Assert.All(reports, value => Assert.InRange(value, 0, 1));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(ModelDirectory()),
            path => path.EndsWith(".partial", StringComparison.Ordinal)
        );
        host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Once);

        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task ChecksumMismatchNeverBecomesAModelFile()
    {
        var files = ModelBytes();
        var server = new FileServer(files);
        var definitions = Definitions(files)
            .Select(file =>
                file.Name == GigaAmModelFiles.Joint ? file with { Sha256 = new string('0', 64) } : file
            )
            .ToArray();
        var http = new HttpClient(server);
        using var plugin = new GigaAmPlugin(
            http,
            (_, _) => new FakeRecognizer(_ => ""),
            new GigaAmModelAssets(http, FixtureUrl, definitions, _ => null)
        );
        await plugin.ActivateAsync(Host().Object);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None)
        );

        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        Assert.False(File.Exists(Path.Join(ModelDirectory(), GigaAmModelFiles.Joint)));
        Assert.False(File.Exists(Path.Join(ModelDirectory(), GigaAmModelFiles.Joint + ".partial")));
    }

    [Fact]
    public async Task TruncatedFileIsDownloadedAgain()
    {
        var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out _);
        await plugin.ActivateAsync(Host().Object);
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Join(ModelDirectory(), GigaAmModelFiles.Encoder), "x");

        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);

        Assert.True(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        Assert.Equal(5, server.Requests.Count);
    }

    [Fact]
    public async Task DownloadStopsBeforeRequestingWhenTheDiskIsFull()
    {
        var files = ModelBytes();
        var server = new FileServer(files);
        var http = new HttpClient(server);
        using var plugin = new GigaAmPlugin(
            http,
            (_, _) => new FakeRecognizer(_ => ""),
            new GigaAmModelAssets(http, FixtureUrl, Definitions(files), _ => 1024)
        );
        await plugin.ActivateAsync(Host().Object);

        await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None)
        );

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task LoadingRequiresTheDownloadedModel()
    {
        var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out _);
        await plugin.ActivateAsync(Host().Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.LoadModelAsync(GigaAmPlugin.ModelId, CancellationToken.None)
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            plugin.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None)
        );
        Assert.Throws<ArgumentException>(() => plugin.SelectModel("parakeet-tdt-0.6b"));
    }

    [Fact]
    public async Task TranscribesLongAudioInChunksAsRussian()
    {
        var replies = new Queue<string>(
            ["Спустя десять лет", "Эта истина верна.", "", "Новое предложение."]
        );
        var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out var recognizers, _ => replies.Dequeue());
        await plugin.ActivateAsync(Host().Object);
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);
        plugin.SelectModel(GigaAmPlugin.ModelId);

        var result = await plugin.TranscribeAsync(
            Wav(SampleRate, Noise(70 * SampleRate)),
            "de",
            false,
            null,
            CancellationToken.None
        );

        var recognizer = Assert.Single(recognizers);
        Assert.Equal(4, recognizer.Lengths.Count);
        Assert.All(
            recognizer.Lengths,
            length => Assert.InRange(length, 1, GigaAmChunking.MaximumChunkSeconds * SampleRate)
        );
        Assert.Equal("Спустя десять лет эта истина верна. Новое предложение.", result.Text);
        Assert.Equal("ru", result.DetectedLanguage);
        Assert.Equal(70, result.DurationSeconds, 3);
        Assert.Equal(
            ["Спустя десять лет", "эта истина верна.", "Новое предложение."],
            result.Segments.Select(segment => segment.Text)
        );
        Assert.Equal(0, result.Segments[0].Start);
        Assert.Equal(70, result.Segments[^1].End, 3);
    }

    [Fact]
    public async Task TranslationAndEmptyAudioAreHandled()
    {
        var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out var recognizers);
        await plugin.ActivateAsync(Host().Object);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            plugin.TranscribeAsync(Wav(SampleRate, new short[SampleRate]), "ru", true, null, CancellationToken.None)
        );
        // No model is needed to transcribe nothing.
        var empty = await plugin.TranscribeAsync(Wav(SampleRate, []), "ru", false, null, CancellationToken.None);
        Assert.Equal("", empty.Text);
        Assert.Empty(recognizers);
    }

    [Fact]
    public async Task DeletingTheModelReleasesTheRecognizerAndTheSelection()
    {
        var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out var recognizers);
        var host = Host();
        await plugin.ActivateAsync(host.Object);
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);
        plugin.SelectModel(GigaAmPlugin.ModelId);
        await plugin.LoadModelAsync(GigaAmPlugin.ModelId, CancellationToken.None);
        Assert.Equal(GigaAmPlugin.PreprocessorPath, Assert.Single(recognizers).PreprocessorPath);

        await plugin.DeleteModelAsync(GigaAmPlugin.ModelId, CancellationToken.None);

        Assert.True(recognizers[0].Disposed);
        Assert.Null(plugin.SelectedModelId);
        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
        Assert.False(Directory.Exists(ModelDirectory()));
        host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Exactly(2));
    }

    [Fact]
    public async Task DeactivationReleasesTheRecognizer()
    {
        var server = new FileServer(ModelBytes());
        using var plugin = Plugin(server, out var recognizers);
        await plugin.ActivateAsync(Host().Object);
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);
        await plugin.LoadModelAsync(GigaAmPlugin.ModelId, CancellationToken.None);

        await plugin.DeactivateAsync();

        Assert.True(Assert.Single(recognizers).Disposed);
        Assert.False(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
    }

    [Fact]
    public async Task DeactivationCancelsADownloadInFlight()
    {
        var server = new FileServer(ModelBytes()) { Stall = true };
        using var plugin = Plugin(server, out _);
        var host = Host();
        await plugin.ActivateAsync(host.Object);
        var download = plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);
        await server.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await plugin.DeactivateAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        server.Stall = false;
        await plugin.ActivateAsync(host.Object);
        await plugin.DownloadModelAsync(GigaAmPlugin.ModelId, null, CancellationToken.None);
        Assert.True(plugin.IsModelDownloaded(GigaAmPlugin.ModelId));
    }

    [Fact]
    public void WavFilesAreReadFromTheirDataChunk()
    {
        var wav = Wav(SampleRate, [0, 16384, -32768], extraChunk: true);

        Assert.Equal([0f, 0.5f, -1f], GigaAmPlugin.DecodeWav(wav));
        Assert.Throws<ArgumentException>(() => GigaAmPlugin.DecodeWav(Wav(44100, [0])));
        Assert.Throws<ArgumentException>(() =>
            GigaAmPlugin.DecodeWav("not a wav file"u8.ToArray())
        );
    }

    [Fact]
    public void StreamedWavWithUnknownSizesIsRead()
    {
        // ffmpeg writing to a pipe cannot seek back, so both sizes stay 0xFFFFFFFF.
        var wav = Wav(SampleRate, [16384, -16384]);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), uint.MaxValue);

        Assert.Equal([0.5f, -0.5f], GigaAmPlugin.DecodeWav(wav));
    }

    // Every plugin copies its manifest.json into this test project's output, so read the source.
    private static string SourceManifestPath([CallerFilePath] string thisFile = "") =>
        Path.Join(
            Path.GetDirectoryName(thisFile),
            "..",
            "..",
            "plugins",
            "TypeWhisper.Plugin.GigaAm",
            "manifest.json"
        );

    private GigaAmPlugin Plugin(
        FileServer server,
        out List<FakeRecognizer> recognizers,
        Func<float[], string>? transcribe = null
    )
    {
        var created = new List<FakeRecognizer>();
        recognizers = created;
        var http = new HttpClient(server);
        return new GigaAmPlugin(
            http,
            (directory, preprocessor) =>
            {
                Assert.Equal(ModelDirectory(), directory);
                var recognizer = new FakeRecognizer(transcribe ?? (_ => ""))
                {
                    PreprocessorPath = preprocessor,
                };
                created.Add(recognizer);
                return recognizer;
            },
            new GigaAmModelAssets(http, FixtureUrl, Definitions(server.Files), _ => null)
        );
    }

    private Mock<IPluginHostServices> Host()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(Path.Join(_root, "data"));
        host.Setup(h => h.PluginAssetDirectory).Returns(Path.Join(_root, "data"));
        return host;
    }

    private string ModelDirectory() => Path.Join(_root, "data", "Models", GigaAmPlugin.ModelId);

    private static Dictionary<string, byte[]> ModelBytes() =>
        new()
        {
            [GigaAmModelFiles.Encoder] = "encoder weights"u8.ToArray(),
            [GigaAmModelFiles.Decoder] = "decoder weights"u8.ToArray(),
            [GigaAmModelFiles.Joint] = "joint weights"u8.ToArray(),
            [GigaAmModelFiles.Vocabulary] = "▁да 0\n<blk> 1\n"u8.ToArray(),
        };

    private static GigaAmModelFile[] Definitions(IReadOnlyDictionary<string, byte[]> files) =>
        files
            .Select(file => new GigaAmModelFile(
                file.Key,
                file.Value.Length,
                Convert.ToHexStringLower(SHA256.HashData(file.Value))
            ))
            .ToArray();

    private static short[] Noise(int length)
    {
        var random = new Random(7);
        var samples = new short[length];
        for (var index = 0; index < length; index++)
        {
            samples[index] = (short)((random.NextDouble() - 0.5) * 6000);
        }

        return samples;
    }

    private static byte[] Wav(int sampleRate, short[] samples, bool extraChunk = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var list = extraChunk ? 12 : 0;
        writer.Write("RIFF"u8);
        writer.Write(36 + list + samples.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        if (extraChunk)
        {
            writer.Write("LIST"u8);
            writer.Write(4);
            writer.Write("INFO"u8);
        }

        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private sealed class FakeRecognizer(Func<float[], string> transcribe) : IGigaAmRecognizer
    {
        public List<int> Lengths { get; } = [];
        public string? PreprocessorPath { get; init; }
        public bool Disposed { get; private set; }

        public string Transcribe(float[] samples, CancellationToken cancellationToken)
        {
            Lengths.Add(samples.Length);
            return transcribe(samples);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FileServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files => files;
        public List<string> Requests { get; } = [];

        // Holds every request until it is cancelled, like a server that never answers.
        public bool Stall { get; set; }
        public TaskCompletionSource Stalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var name = request.RequestUri!.Segments[^1];
            lock (Requests)
            {
                Requests.Add(name);
            }

            // ReSharper disable once InvertIf -- inverting would duplicate the response below.
            if (Stall)
            {
                Stalled.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return files.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
