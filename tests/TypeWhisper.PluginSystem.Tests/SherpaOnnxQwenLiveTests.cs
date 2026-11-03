using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Runs only when <c>TYPEWHISPER_SHERPA_LIVE_DIR</c> names a scratch plugin asset directory,
///     <c>TYPEWHISPER_SHERPA_PLUGIN_DIR</c> the staged Plugins directory (loaded through the host's
///     loader as in the app, so the plugin gets its own ONNX Runtime) and
///     <c>TYPEWHISPER_SHERPA_RECORDINGS</c> a directory of 16 kHz mono PCM16 WAV files. Downloads the
///     pinned Qwen3-ASR models (~1 GB and ~2.4 GB) into it if missing, transcribes every recording
///     with each and writes <c>&lt;model&gt;.tsv</c> there. Optional: <c>TYPEWHISPER_QWEN_MODELS</c>
///     (comma-separated ids; one per run gives that model's peak memory),
///     <c>TYPEWHISPER_QWEN_GAIN</c> (scales every recording, to test quiet input),
///     <c>TYPEWHISPER_QWEN_WHISPER_MODE</c> (<c>1</c> applies the recorder's whisper-mode gain after that),
///     <c>TYPEWHISPER_QWEN_LANGUAGE</c> (an explicit language code; a model that needs one gets
///     <c>en</c> without it), <c>TYPEWHISPER_SHERPA_BACKEND</c>
///     (<c>cuda</c>) and <c>TYPEWHISPER_SHERPA_REFERENCES</c> (a TSV of recording name and reference
///     text, for word error rates).
/// </summary>
public sealed class SherpaOnnxQwenLiveTests
{
    private static readonly string[] s_variables =
    [
        "TYPEWHISPER_SHERPA_LIVE_DIR",
        "TYPEWHISPER_SHERPA_PLUGIN_DIR",
        "TYPEWHISPER_SHERPA_RECORDINGS",
    ];

    private static string LiveDir => Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_LIVE_DIR")!;

    [QwenLiveFact]
    public async Task QwenModels_DownloadVerifiedAndTranscribeRecordings()
    {
        var models = (Environment.GetEnvironmentVariable("TYPEWHISPER_QWEN_MODELS")
                ?? "qwen3-asr-0.6b,qwen3-asr-1.7b")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var gainText = Environment.GetEnvironmentVariable("TYPEWHISPER_QWEN_GAIN");
        var gain = gainText is null ? 1f : float.Parse(gainText, CultureInfo.InvariantCulture);
        var whisperMode = Environment.GetEnvironmentVariable("TYPEWHISPER_QWEN_WHISPER_MODE") == "1";
        var language = Environment.GetEnvironmentVariable("TYPEWHISPER_QWEN_LANGUAGE");
        var backend = Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_BACKEND") ?? "cpu";
        var references = ReadReferences(Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_REFERENCES"));
        var recordings = Directory.GetFiles(Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_RECORDINGS")!, "*.wav")
            .Order(StringComparer.Ordinal)
            .ToArray();

        var loader = new PluginLoader(Path.Join(LiveDir, "loader-data"));
        var loaded = loader.DiscoverAndLoad([Environment.GetEnvironmentVariable("TYPEWHISPER_SHERPA_PLUGIN_DIR")!]);
        var plugin = (ITranscriptionEnginePlugin)Assert.Single(
            loaded, p => p.Manifest.Id == "com.typewhisper.sherpa-onnx").Instance;
        await plugin.ActivateAsync(new SupertonicTtsPluginTests.TestPluginHostServices { PluginDataDirectory = LiveDir });
        await plugin.ConfigureComputeBackendAsync(backend);
        foreach (var modelId in models)
        {
            var downloadTimer = Stopwatch.StartNew();
            await plugin.DownloadModelAsync(modelId, null, CancellationToken.None);
            Assert.True(plugin.IsModelDownloaded(modelId));
            var downloadSeconds = downloadTimer.Elapsed.TotalSeconds;

            var loadTimer = Stopwatch.StartNew();
            await plugin.LoadModelAsync(modelId, CancellationToken.None);
            var loadSeconds = loadTimer.Elapsed.TotalSeconds;
            Assert.Equal(modelId, plugin.SelectedModelId);
            // A model that cannot detect the language itself (Qwen 1.7B) is run in English.
            var modelLanguage = language
                ?? (plugin is ITranscriptionLanguageSelectionCapabilities
                    { AutomaticDetectionSupport: LanguageSelectionSupport.Unsupported } ? "en" : null);
            var suffix = (gainText is null ? "" : $"-gain{gainText}")
                + (whisperMode ? "-whisper" : "")
                + (modelLanguage is null ? "" : $"-{modelLanguage}")
                + (backend == "cpu" ? "" : $"-{backend}");

            var report = new StringBuilder("recording\tseconds\tdecode\ttimings\twer_edits\twer_words\ttext\n");
            double audioSeconds = 0, decodeSeconds = 0;
            int empty = 0, markers = 0, edits = 0, words = 0, scored = 0;
            foreach (var path in recordings)
            {
                var wav = await File.ReadAllBytesAsync(path);
                if (gainText is not null || whisperMode)
                    wav = Prepare(wav, gain, whisperMode);
                var seconds = (wav.Length - 44) / 32000d;
                if (seconds < 0.5)
                    continue;
                var timer = Stopwatch.StartNew();
                var result = await plugin.TranscribeAsync(wav, modelLanguage, false, null, CancellationToken.None);
                var elapsed = timer.Elapsed.TotalSeconds;
                audioSeconds += seconds;
                decodeSeconds += elapsed;
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(result.Text))
                    empty++;
                if (result.Text.Contains("<asr_text>", StringComparison.Ordinal)
                    || result.Text.StartsWith("language ", StringComparison.Ordinal))
                    markers++;

                var (e, w) = (0, 0);
                if (references.TryGetValue(name, out var reference))
                {
                    var expected = Words(reference);
                    (e, w) = (EditDistance(expected, Words(result.Text)), expected.Length);
                    edits += e;
                    words += w;
                    scored++;
                }

                report.Append(CultureInfo.InvariantCulture,
                    $"{name}\t{seconds:0.0}\t{elapsed:0.00}\t{result.TokenTimings.Count}\t{e}\t{w}\t{result.Text.ReplaceLineEndings(" ")}\n");
            }

            await File.WriteAllTextAsync(Path.Join(LiveDir, $"{modelId}{suffix}.tsv"), report.ToString());
            await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{modelId}{suffix} ({plugin.AccelerationStatus.DisplayText}): download {downloadSeconds:0}s, load {loadSeconds:0.0}s, "
                + $"{audioSeconds:0} s of audio in {decodeSeconds:0} s (RTF {decodeSeconds / Math.Max(1, audioSeconds):0.000}), "
                + $"{empty} empty, {markers} with markers, WER {(words == 0 ? double.NaN : 100d * edits / words):0.00}% over {scored} references, "
                + $"peak RSS {PeakResidentMegabytes()} MB"));
            Assert.Equal(0, markers);
        }

        await plugin.DeactivateAsync();
        plugin.Dispose();
    }

    private static Dictionary<string, string> ReadReferences(string? path) =>
        path is null
            ? []
            : File.ReadLines(path)
                .Select(line => line.Split('\t', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    // Scales a plain PCM16 WAV and optionally runs the recorder's whisper-mode gain over it in
    // capture-sized buffers, as live dictation would; the header is kept.
    private static byte[] Prepare(byte[] wav, float gain, bool whisperMode)
    {
        const int framesPerBuffer = 512;
        var processed = wav.ToArray();
        var data = FindData(processed);
        var samples = new float[(processed.Length - data) / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(processed.AsSpan(data + 2 * i, 2)) / 32768f * gain;
        if (whisperMode)
        {
            for (var start = 0; start < samples.Length; start += framesPerBuffer)
            {
                var buffer = samples[start..Math.Min(samples.Length, start + framesPerBuffer)];
                AudioRecordingService.ApplyWhisperModeGain(buffer, whisperModeEnabled: true).CopyTo(samples, start);
            }
        }

        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(
                processed.AsSpan(data + 2 * i, 2),
                (short)Math.Clamp(samples[i] * 32768f, short.MinValue, short.MaxValue));
        return processed;
    }

    private static int FindData(byte[] wav)
    {
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            if (Encoding.ASCII.GetString(wav, pos, 4) == "data")
                return pos + 8;
            pos += 8 + size + (size & 1);
        }

        throw new InvalidDataException("No data chunk.");
    }

    // Linux reports the process's high-water resident set as VmHWM.
    private static long PeakResidentMegabytes()
    {
        var line = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("VmHWM:", StringComparison.Ordinal));
        return line is null ? -1 : long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1024;
    }

    private static string[] Words(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '\'' ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

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

    private sealed class QwenLiveFactAttribute : FactAttribute
    {
        public QwenLiveFactAttribute()
        {
            if (s_variables.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Set TYPEWHISPER_SHERPA_LIVE_DIR, TYPEWHISPER_SHERPA_PLUGIN_DIR and TYPEWHISPER_SHERPA_RECORDINGS to download and run Qwen3-ASR.";
        }
    }
}
