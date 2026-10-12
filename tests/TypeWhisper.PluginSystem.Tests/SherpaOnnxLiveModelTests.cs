extern alias SherpaOnnx;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Runs only when <c>TYPEWHISPER_SHERPA_LIVE_DIR</c> names a scratch plugin asset directory.
///     Both tests fetch the pinned Parakeet Ultra files (~670 MB) into it if missing. The recording
///     test also needs <c>TYPEWHISPER_SHERPA_PLUGIN_DIR</c> (the staged Plugins directory, loaded
///     through the host's loader as in the app), <c>TYPEWHISPER_SHERPA_RECORDINGS</c> (a directory
///     of 16 kHz mono WAV files) and <c>TYPEWHISPER_CTC_MODEL_DIR</c> (a prepared CTC model). It
///     transcribes every recording with each Parakeet model present under <c>Models</c>, boosts
///     the results with a fixed term list, and writes per-recording results there. Run it with
///     <c>XDG_DATA_HOME</c> inside the scratch directory: activation migrates model folders from
///     the legacy data directory.
/// </summary>
public sealed class SherpaOnnxLiveModelTests
{
    private static readonly string[] s_terms =
    [
        "TypeWhisper",
        "Parakeet",
        "CodeRabbit",
        "ReSharper",
        "Avalonia",
        "Supertonic",
        "sherpa-onnx",
        "Claude",
        "Gemma",
        "Linux",
        "GitHub",
        "Fedora",
    ];

    private static readonly string[] s_parakeetModels = ["parakeet-ultra-0.6b", "parakeet-tdt-0.6b"];

    private static readonly string[] s_recordingVariables =
    [
        "TYPEWHISPER_SHERPA_PLUGIN_DIR",
        "TYPEWHISPER_SHERPA_RECORDINGS",
        "TYPEWHISPER_CTC_MODEL_DIR",
    ];

    private static string LiveDir => Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_LIVE_DIR")!;

    [SherpaLiveFact]
    public async Task UltraDownload_ResumesAfterADroppedConnectionAndVerifies()
    {
        var modelDir = Path.Join(LiveDir, "Models", "parakeet-ultra-0.6b");
        if (Directory.Exists(modelDir))
            Directory.Delete(modelDir, recursive: true);
        var ranges = new List<string?>();
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(new SupertonicTtsPluginTests.TestPluginHostServices { PluginDataDirectory = LiveDir });
        var handler = new RecordingHandler(ranges) { DropEncoderAfterBytes = 200_000_000 };
        plugin.SetDownloadSourceForTests(handler, SherpaOnnxPlugin.DefaultModelsForTests);
        var encoderPartial = Path.Join(modelDir, "encoder.int8.onnx.partial");

        await Assert.ThrowsAsync<IOException>(() =>
            plugin.DownloadModelAsync("parakeet-ultra-0.6b", null, CancellationToken.None));
        var kept = new FileInfo(encoderPartial).Length;

        await plugin.DownloadModelAsync("parakeet-ultra-0.6b", null, CancellationToken.None);

        Assert.True(plugin.IsModelDownloaded("parakeet-ultra-0.6b"));
        Assert.Contains($"bytes={kept}-", ranges);
        Assert.Empty(Directory.GetFiles(modelDir, "*.partial"));
        await Console.Out.WriteLineAsync(
            $"Resumed encoder.int8.onnx at {kept} bytes; ranges: {string.Join(", ", ranges.Where(r => r is not null))}");

        // A user who cancels gets the space back instead of a partial the app cannot delete. Uses
        // its own directory so the verified model stays for the recording test.
        var cancelRoot = Path.Join(LiveDir, "cancel-check");
        if (Directory.Exists(cancelRoot))
            Directory.Delete(cancelRoot, recursive: true);
        using var cancelling = new SherpaOnnxPlugin();
        cancelling.SetHostForTests(new SupertonicTtsPluginTests.TestPluginHostServices { PluginDataDirectory = cancelRoot });
        var cancelledPartial = Path.Join(cancelRoot, "Models", "parakeet-ultra-0.6b", "encoder.int8.onnx.partial");
        using var cts = new CancellationTokenSource();
        // ReSharper disable AccessToDisposedClosure -- the watcher is awaited below, before cts is disposed.
        var watcher = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(cancelledPartial) && new FileInfo(cancelledPartial).Length > 50_000_000)
                {
                    await cts.CancelAsync();
                    return;
                }

                await Task.Delay(50, CancellationToken.None);
            }
        }, CancellationToken.None);
        // ReSharper restore AccessToDisposedClosure
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelling.DownloadModelAsync("parakeet-ultra-0.6b", null, cts.Token));
        await watcher;
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cancelledPartial)!, "*.partial"));
        Directory.Delete(cancelRoot, recursive: true);
    }

    [SherpaLiveFact(RequiresRecordings = true)]
    public async Task PackagedPlugin_TranscribesRecordingsWithValidTimingsAndBoosts()
    {
        var pluginsDir = Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_PLUGIN_DIR")!;
        var recordings = Directory.GetFiles(Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_RECORDINGS")!, "*.wav")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var loader = new PluginLoader(Path.Join(LiveDir, "loader-data"));
        var loadedPlugins = loader.DiscoverAndLoad([pluginsDir]);
        var sherpa = (ITranscriptionEnginePlugin)Assert.Single(
            loadedPlugins, p => p.Manifest.Id == "com.typewhisper.sherpa-onnx").Instance;
        var ctc = (IVocabularyRescorerPlugin)Assert.Single(
            loadedPlugins, p => p.Manifest.Id == "com.typewhisper.parakeet-ctc").Instance;
        Assert.NotSame(typeof(SherpaOnnxPlugin).Assembly, sherpa.GetType().Assembly);
        await sherpa.ActivateAsync(new SupertonicTtsPluginTests.TestPluginHostServices { PluginDataDirectory = LiveDir });
        var ctcHost = new LoggingHost { PluginDataDirectory = Path.Join(LiveDir, "ctc-data") };
        ctcHost.SetSetting("ModelDirectory", Environment.GetEnvironmentVariable("TYPEWHISPER_CTC_MODEL_DIR")!);
        await ctc.ActivateAsync(ctcHost);
        Assert.True(ctc.IsReady);
        var terms = s_terms.Select(term => new VocabularyTermHint(term)).ToArray();

        // A no-op when the download test already fetched it.
        await sherpa.DownloadModelAsync("parakeet-ultra-0.6b", null, CancellationToken.None);
        var models = s_parakeetModels.Where(sherpa.IsModelDownloaded).ToArray();
        Assert.Contains("parakeet-ultra-0.6b", models);
        var transcripts = new Dictionary<string, Dictionary<string, string>>();
        foreach (var modelId in models)
        {
            await sherpa.LoadModelAsync(modelId, CancellationToken.None);
            var texts = transcripts[modelId] = new Dictionary<string, string>(StringComparer.Ordinal);
            var report = new StringBuilder("recording\tseconds\ttimings\tboost\treplacements\ttext\n");
            int empty = 0, withTimings = 0, longOnes = 0, longWithTimings = 0, boosted = 0, skipped = 0, replaced = 0;
            foreach (var path in recordings)
            {
                var wav = await File.ReadAllBytesAsync(path);
                var samples = ReadMono16K(wav);
                var seconds = samples.Length / 16000d;
                if (seconds < 0.5)
                    continue;
                var result = await sherpa.TranscribeAsync(wav, null, false, null, CancellationToken.None);
                var name = Path.GetFileNameWithoutExtension(path);
                texts[name] = result.Text;
                if (string.IsNullOrWhiteSpace(result.Text))
                    empty++;
                AssertTimingsValid(result.TokenTimings, seconds, name);
                if (result.TokenTimings.Count > 0)
                    withTimings++;
                // A recording without speech has neither text nor timings.
                if (seconds > 15 && !string.IsNullOrWhiteSpace(result.Text))
                {
                    longOnes++;
                    if (result.TokenTimings.Count > 0)
                        longWithTimings++;
                }

                var outcome = "none";
                var count = 0;
                if (result.TokenTimings.Count > 0 && !string.IsNullOrWhiteSpace(result.Text))
                {
                    var id = Guid.NewGuid();
                    var rescored = await ctc.RescoreAsync(
                        new VocabularyRescoreRequest(id, result.Text, samples, 16000, result.TokenTimings, terms),
                        CancellationToken.None);
                    var skip = ctcHost.Messages.LastOrDefault(m => m.StartsWith($"{id} plugin-skipped", StringComparison.Ordinal));
                    // Only malformed requests are failures; "no candidates" style skips are normal.
                    Assert.False(
                        skip is not null && (skip.Contains("invalid-timing", StringComparison.Ordinal)
                            || skip.Contains("token-text-alignment", StringComparison.Ordinal)),
                        $"{modelId} {name}: {skip}");
                    count = rescored.Replacements.Count;
                    outcome = skip is null ? "ran" : skip[(skip.IndexOf("reason=", StringComparison.Ordinal) + 7)..];
                    if (skip is null)
                        boosted++;
                    else
                        skipped++;
                    replaced += count;
                }

                report.Append(CultureInfo.InvariantCulture, $"{name}\t{seconds:0.0}\t{result.TokenTimings.Count}\t{outcome}\t{count}\t{result.Text.ReplaceLineEndings(" ")}\n");
            }

            await File.WriteAllTextAsync(Path.Join(LiveDir, $"{modelId}.tsv"), report.ToString());
            await Console.Out.WriteLineAsync(
                $"{modelId}: {texts.Count} recordings, {empty} empty, {withTimings} with timings ({longWithTimings}/{longOnes} transcripts over 15 s), "
                + $"boosting ran on {boosted} and skipped {skipped}, {replaced} replacements");
            Assert.Equal(longOnes, longWithTimings);
        }

        if (transcripts.Count == 2)
        {
            var ultra = transcripts["parakeet-ultra-0.6b"];
            var tdt = transcripts["parakeet-tdt-0.6b"];
            int identical = 0, wordEdits = 0, words = 0;
            foreach (var (name, text) in tdt)
            {
                var reference = Words(text);
                words += reference.Length;
                var distance = EditDistance(reference, Words(ultra[name]));
                wordEdits += distance;
                if (distance == 0)
                    identical++;
            }

            await Console.Out.WriteLineAsync(
                $"Ultra vs TDT: {identical}/{tdt.Count} identical, {wordEdits} word edits over {words} TDT words");
        }

        await sherpa.DeactivateAsync();
        await ctc.DeactivateAsync();
        sherpa.Dispose();
        ctc.Dispose();
    }

    private static void AssertTimingsValid(IReadOnlyList<VocabularyTokenTiming> timings, double seconds, string name)
    {
        var previousStart = 0d;
        foreach (var timing in timings)
        {
            Assert.True(double.IsFinite(timing.StartSeconds) && double.IsFinite(timing.EndSeconds), name);
            Assert.True(timing.StartSeconds >= previousStart, $"{name}: start {timing.StartSeconds} before {previousStart}");
            Assert.True(timing.EndSeconds > timing.StartSeconds, $"{name}: empty token at {timing.StartSeconds}");
            Assert.True(timing.EndSeconds <= seconds, $"{name}: token ends at {timing.EndSeconds} after {seconds}");
            previousStart = timing.StartSeconds;
        }
    }

    private static string[] Words(string text) =>
        text.ToLowerInvariant()
            .Split((char[])[' ', '\n', '\t', ',', '.', '?', '!', ';', ':', '"'], StringSplitOptions.RemoveEmptyEntries);

    private static int EditDistance(string[] a, string[] b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }

        return row[b.Length];
    }

    // The app records 16 kHz mono 16-bit PCM.
    private static float[] ReadMono16K(byte[] wav)
    {
        var span = wav.AsSpan();
        var pos = 12;
        while (pos + 8 <= span.Length)
        {
            var id = Encoding.ASCII.GetString(span.Slice(pos, 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            switch (id)
            {
                case "fmt ":
                    Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(span.Slice(pos + 10, 2)));
                    Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 12, 4)));
                    Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(span.Slice(pos + 22, 2)));
                    break;
                case "data":
                {
                    var data = span.Slice(pos + 8, Math.Min(size < 0 ? int.MaxValue : size, span.Length - pos - 8));
                    var samples = new float[data.Length / 2];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2)) / 32768f;
                    return samples;
                }
            }

            pos += 8 + size + (size & 1);
        }

        throw new InvalidDataException("No data chunk.");
    }

    private sealed class LoggingHost : IPluginHostServices
    {
        private readonly SupertonicTtsPluginTests.TestPluginHostServices _inner = new();
        public List<string> Messages { get; } = [];
        public required string PluginDataDirectory { get; init; }
        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public T? GetSetting<T>(string key) => _inner.GetSetting<T>(key);
        public void SetSetting<T>(string key, T value) => _inner.SetSetting(key, value);
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus => _inner.EventBus;
        public IReadOnlyList<string> AvailableProfileNames => [];

        public void Log(PluginLogLevel level, string message)
        {
            lock (Messages)
                Messages.Add(message);
        }

        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization => _inner.Localization;
    }

    private sealed class RecordingHandler(List<string?> ranges) : DelegatingHandler(new HttpClientHandler())
    {
        public long? DropEncoderAfterBytes { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (ranges)
                ranges.Add(request.Headers.Range?.ToString());
            // Read before sending: following a redirect rewrites RequestUri to the storage host.
            var isEncoder = request.RequestUri!.AbsolutePath.EndsWith("/encoder.int8.onnx", StringComparison.Ordinal);
            var response = await base.SendAsync(request, ct);
            if (DropEncoderAfterBytes is not { } limit || !isEncoder)
                return response;

            DropEncoderAfterBytes = null;
            var dropping = new StreamContent(new DroppingStream(await response.Content.ReadAsStreamAsync(ct), limit));
            foreach (var header in response.Content.Headers)
                dropping.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = dropping;
            return response;
        }
    }

    // Ends the body with a network error after a number of bytes, like a dropped connection.
    private sealed class DroppingStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read >= limit)
                throw new IOException("Simulated dropped connection.");
            var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read)], ct);
            _read += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SherpaLiveFactAttribute : FactAttribute
    {
        public SherpaLiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_LIVE_DIR")))
                Skip = "Set TYPEWHISPER_SHERPA_LIVE_DIR to a scratch directory to download and run Parakeet Ultra.";
        }

        public bool RequiresRecordings
        {
            get;
            set
            {
                field = value;
                if (value && Skip is null
                    && s_recordingVariables.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                    Skip = "Set TYPEWHISPER_SHERPA_PLUGIN_DIR, TYPEWHISPER_SHERPA_RECORDINGS and TYPEWHISPER_CTC_MODEL_DIR to transcribe recordings.";
            }
        }
    }
}
