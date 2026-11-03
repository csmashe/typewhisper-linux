using System.Buffers.Binary;
using System.Text;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.SupertonicTts;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Runs only when <c>TYPEWHISPER_SUPERTONIC_LIVE_DIR</c> names a scratch directory. The tests
///     download the pinned Supertonic 3 assets (~383 MB) from Hugging Face into it, synthesize
///     real speech on the CPU, and leave the generated WAV files there for listening. Synthesis
///     needs <c>TYPEWHISPER_SUPERTONIC_PLUGIN_DIR</c>: the staged Plugins directory, loaded
///     through the host's loader so ONNX Runtime resolves from the package as in the app.
/// </summary>
public sealed class SupertonicLiveModelTests
{
    private static string LiveDir => Environment.GetEnvironmentVariable("TYPEWHISPER_SUPERTONIC_LIVE_DIR")!;
    private static string AssetRoot => Path.Join(LiveDir, "Models", SupertonicPaths.ModelDirectoryName);

    [SupertonicLiveFact]
    public async Task PinnedDownload_ResumesAfterCancellationAndVerifies()
    {
        if (Directory.Exists(AssetRoot))
            Directory.Delete(AssetRoot, recursive: true);
        var ranges = new List<string?>();
        using var client = new HttpClient(new RecordingHandler(ranges));
        client.Timeout = TimeSpan.FromHours(1);
        var sut = new SupertonicAssetManager(
            AssetRoot,
            client,
            SupertonicAssetManager.DefaultFiles,
            $"https://huggingface.co/Supertone/supertonic-3/resolve/{SupertonicAssetManager.Revision}");
        var vectorPartial = Path.Join(AssetRoot, "onnx", "vector_estimator.onnx.partial");

        // Cancel once the largest file is about a third of the way in.
        using var cts = new CancellationTokenSource();
        // ReSharper disable AccessToDisposedClosure -- the watcher is awaited below, before cts is disposed.
        var watcher = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(vectorPartial) && new FileInfo(vectorPartial).Length > 80_000_000)
                {
                    await cts.CancelAsync();
                    return;
                }

                await Task.Delay(50, CancellationToken.None);
            }
        }, CancellationToken.None);
        // ReSharper restore AccessToDisposedClosure
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.DownloadMissingAssetsAsync(null, cts.Token));
        await watcher;
        var kept = new FileInfo(vectorPartial).Length;

        await sut.DownloadMissingAssetsAsync(null, CancellationToken.None);

        Assert.True(sut.AreAssetsReady);
        Assert.Contains($"bytes={kept}-", ranges);
        var fresh = new SupertonicAssetManager(AssetRoot);
        Assert.True(await fresh.VerifyCachedAssetsAsync(CancellationToken.None));
        await Console.Out.WriteLineAsync($"Resumed vector_estimator.onnx at {kept} bytes; ranges: {string.Join(", ", ranges.Where(r => r is not null))}");
    }

    [SupertonicLiveFact(RequiresPackagedPlugin = true)]
    public async Task PackagedPlugin_SynthesizesSpeechInSeveralLanguagesThenRemovesItsFiles()
    {
        var pluginsDir = Environment.GetEnvironmentVariable("TYPEWHISPER_SUPERTONIC_PLUGIN_DIR")!;
        var loader = new PluginLoader(Path.Join(Path.GetTempPath(), "tw-supertonic-plugin-data-" + Guid.NewGuid().ToString("N")));
        var loaded = Assert.Single(
            loader.DiscoverAndLoad([pluginsDir]),
            p => p.Manifest.Id == "com.typewhisper.supertonic-tts");
        Assert.NotSame(typeof(SupertonicTtsPlugin).Assembly, loaded.Instance.GetType().Assembly);
        var plugin = (ITtsProviderPlugin)loaded.Instance;
        var actions = (IPluginSettingsActions)loaded.Instance;
        var playback = new RecordingPcmPlaybackService();
        var host = new SupertonicTtsPluginTests.TestPluginHostServices { PluginDataDirectory = LiveDir, PcmPlayback = playback };
        await plugin.ActivateAsync(host);
        await ((IPluginSettingsProvider)plugin).SetSettingValueAsync(SupertonicTtsPlugin.LicenseAcceptedSettingName, "true");
        var download = await actions.ExecuteSettingsActionAsync(SupertonicTtsPlugin.DownloadActionId, CancellationToken.None);
        Assert.True(download.IsSuccess, download.Message);
        Assert.True(plugin.IsConfigured);

        (string Language, string Text)[] samples =
        [
            ("en", "Hello, this is TypeWhisper reading your text out loud on this computer."),
            ("de", "Guten Morgen, das ist eine kurze Probe der lokalen Sprachausgabe."),
            ("es", "Buenos días, esta es una breve prueba de la voz local."),
        ];
        foreach (var (language, text) in samples)
        {
            var session = await plugin.SpeakAsync(new TtsSpeakRequest(text, language), CancellationToken.None);
            Assert.True(session.IsActive);
            var request = playback.Requests[^1];
            var pcm = ToFloats(request.Payload.Span);
            var seconds = pcm.Length / (double)request.SampleRate;
            var rms = Math.Sqrt(pcm.Average(sample => (double)sample * sample));
            Assert.All(pcm, sample => Assert.True(float.IsFinite(sample)));
            Assert.InRange(seconds, 1.5, 15);
            Assert.True(rms > 0.01, $"{language}: speech is nearly silent (RMS {rms:0.0000}).");
            await WriteWavAsync(Path.Join(LiveDir, $"supertonic-{language}.wav"), pcm, request.SampleRate);
            await Console.Out.WriteLineAsync($"{language}: {seconds:0.00} s, RMS {rms:0.000}");
        }

        Assert.True(actions.GetSettingsActions().Single(action => action.Id == SupertonicTtsPlugin.RemoveActionId).IsEnabled);
        var removed = await actions.ExecuteSettingsActionAsync(SupertonicTtsPlugin.RemoveActionId, CancellationToken.None);

        Assert.True(removed.IsSuccess, removed.Message);
        Assert.False(plugin.IsConfigured);
        Assert.False(Directory.Exists(AssetRoot));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.SpeakAsync(new TtsSpeakRequest("Gone", "en"), CancellationToken.None));
        await plugin.DeactivateAsync();
        plugin.Dispose();
    }

    private static float[] ToFloats(ReadOnlySpan<byte> payload)
    {
        var samples = new float[payload.Length / sizeof(float)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(i * sizeof(float), sizeof(float)));
        return samples;
    }

    private static async Task WriteWavAsync(string path, float[] samples, int sampleRate)
    {
        await using var stream = File.Create(path);
        await using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
    }

    private sealed class RecordingHandler(List<string?> ranges) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (ranges)
                ranges.Add(request.Headers.Range?.ToString());
            return base.SendAsync(request, ct);
        }
    }

    private sealed class SupertonicLiveFactAttribute : FactAttribute
    {
        public SupertonicLiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_SUPERTONIC_LIVE_DIR")))
                Skip = "Set TYPEWHISPER_SUPERTONIC_LIVE_DIR to a scratch directory to download and run Supertonic 3.";
        }

        public bool RequiresPackagedPlugin
        {
            get;
            set
            {
                field = value;
                if (value && Skip is null
                    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TYPEWHISPER_SUPERTONIC_PLUGIN_DIR")))
                    Skip = "Set TYPEWHISPER_SUPERTONIC_PLUGIN_DIR to the staged Plugins directory to run synthesis.";
            }
        }
    }
}
