using System.Buffers.Binary;
using System.Text.Json;
using TypeWhisper.Plugin.SupertonicTts;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
// ReSharper disable PropertyCanBeMadeInitOnly.Local

namespace TypeWhisper.PluginSystem.Tests;

public class SupertonicTtsPluginTests
{
    private static readonly TimeSpan s_coordinationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Manifest_DeclaresLocalTtsPlugin()
    {
        var manifestPath = FindRepoFile("plugins", "TypeWhisper.Plugin.SupertonicTts", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        Assert.Equal("com.typewhisper.supertonic-tts", root.GetProperty("id").GetString());
        Assert.Equal("Supertonic TTS", root.GetProperty("name").GetString());
        Assert.Equal(["tts"], root.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("local", root.GetProperty("networkAccess").GetString());
        Assert.Equal("TypeWhisper.Plugin.SupertonicTts.dll", root.GetProperty("assemblyName").GetString());
        Assert.Equal("TypeWhisper.Plugin.SupertonicTts.SupertonicTtsPlugin", root.GetProperty("pluginClass").GetString());
    }

    [Fact]
    public async Task ActivateAsync_NormalizesPersistedSettingsAndExposesProviderDefaults()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = false };
        var host = new TestPluginHostServices();
        host.SetSetting("selectedVoice", "unknown");
        host.SetSetting("speed", 9.0);
        host.SetSetting("denoisingSteps", 0);
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());

        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.supertonic-tts", sut.PluginId);
        Assert.Equal("supertonic-tts", sut.ProviderId);
        Assert.Equal("Supertonic TTS", sut.ProviderDisplayName);
        Assert.False(sut.IsConfigured);
        Assert.Equal("M1", sut.SelectedVoiceId);
        Assert.Equal(1.5, sut.Speed);
        Assert.Equal(1, sut.DenoisingSteps);
        Assert.Equal(10, sut.AvailableVoices.Count);
        Assert.Contains(sut.AvailableVoices, voice => voice.Id == "F5");
    }

    [Fact]
    public async Task DownloadAssetsAsync_RequiresLicenseConfirmationBeforeDownload()
    {
        var assets = new FakeSupertonicAssets();
        var host = new TestPluginHostServices();
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DownloadAssetsAsync(null, CancellationToken.None));

        sut.SetLicenseAccepted(true);
        await sut.DownloadAssetsAsync(null, CancellationToken.None);

        Assert.True(sut.HasAcceptedModelLicense);
        Assert.Equal(1, assets.DownloadCount);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SpeakAsync_EmptyTextReturnsInactiveSessionAndMissingAssetsThrow()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = false };
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices
        {
            PcmPlayback = new RecordingPcmPlaybackService(),
        });

        var empty = await sut.SpeakAsync(new TtsSpeakRequest("   ", "en"), CancellationToken.None);
        Assert.False(empty.IsActive);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello", "en"), CancellationToken.None));
        Assert.Contains("Supertonic 3 assets", ex.Message);
    }

    [Fact]
    public async Task SpeakAsync_SkipsInferenceWhenHostPcmPlaybackIsUnavailable()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true };
        var synthesizer = new FakeSupertonicSynthesizer();
        var sut = new SupertonicTtsPlugin(assets, _ => synthesizer);
        await sut.ActivateAsync(new TestPluginHostServices());

        var session = await sut.SpeakAsync(
            new TtsSpeakRequest("Do not synthesize", "en"),
            CancellationToken.None
        );

        Assert.False(session.IsActive);
        Assert.Null(synthesizer.LastRequest);
        Assert.False(synthesizer.Started.IsCompleted);
    }

    [Fact]
    public async Task SpeakAsync_UsesSynthesizerWithSelectedVoiceLanguageAndSettings()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true, AssetRoot = "/models/supertonic-3" };
        var synth = new FakeSupertonicSynthesizer();
        var playback = new RecordingPcmPlaybackService();
        var sut = new SupertonicTtsPlugin(assets, _ => synth);
        await sut.ActivateAsync(new TestPluginHostServices { PcmPlayback = playback });
        sut.SelectVoice("F3");
        sut.SetSpeed(1.25);
        sut.SetDenoisingSteps(12);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Hallo Welt", "de-DE"), CancellationToken.None);

        Assert.True(session.IsActive);
        Assert.Equal("Hallo Welt", synth.LastRequest?.Text);
        Assert.Equal("de", synth.LastRequest?.Language);
        Assert.EndsWith(Path.Join("voice_styles", "F3.json"), synth.LastRequest?.VoiceStylePath);
        Assert.Equal(1.25, synth.LastRequest?.Speed);
        Assert.Equal(12, synth.LastRequest?.DenoisingSteps);
        var playbackRequest = Assert.Single(playback.Requests);
        Assert.Equal(PcmSampleFormat.Float32, playbackRequest.Format);
        Assert.Equal(24_000, playbackRequest.SampleRate);
        Assert.Equal(1, playbackRequest.Channels);
        Assert.Equal(2 * sizeof(float), playbackRequest.Payload.Length);
        Assert.Equal(
            [0.1f, -0.1f],
            Enumerable.Range(0, 2)
                .Select(i => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(
                        playbackRequest.Payload.Span.Slice(i * sizeof(float), sizeof(float))
                    )
                ))
                .ToArray()
        );
    }

    [Fact]
    public async Task ActivateAsync_VerifiesCachedAssets()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true };
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());

        await sut.ActivateAsync(new TestPluginHostServices());

        Assert.Equal(1, assets.VerifyCount);
        Assert.True(sut.IsConfigured);
    }

    [Fact]
    public async Task ValidateAsync_ReportsStateWithoutDownloading()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = false };
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());

        var unlicensed = await sut.ValidateAsync();
        sut.SetLicenseAccepted(true);
        var missing = await sut.ValidateAsync();
        assets.AreAssetsReadyValue = true;
        var ready = await sut.ValidateAsync();

        Assert.Equal(new PluginSettingsValidationResult(false, "Settings.AcceptLicense"), unlicensed);
        Assert.Equal(new PluginSettingsValidationResult(false, "Settings.DownloadRequired"), missing);
        Assert.Equal(new PluginSettingsValidationResult(true, "Settings.Ready"), ready);
        Assert.Equal(0, assets.DownloadCount);
    }

    [Fact]
    public async Task GetSettingsActions_ReflectsAssetState()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = false };
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());

        var empty = sut.GetSettingsActions();
        assets.HasAnyAssetsValue = true;
        var partial = sut.GetSettingsActions();
        assets.AreAssetsReadyValue = true;
        var ready = sut.GetSettingsActions();

        Assert.Equal(
            [SupertonicTtsPlugin.DownloadActionId, SupertonicTtsPlugin.RemoveActionId],
            empty.Select(action => action.Id));
        Assert.Equal([true, false], empty.Select(action => action.IsEnabled));
        Assert.Equal([true, true], partial.Select(action => action.IsEnabled));
        Assert.Equal([false, true], ready.Select(action => action.IsEnabled));
        Assert.Null(empty[0].ConfirmationMessage);
        Assert.Equal("Settings.RemoveConfirmation", empty[1].ConfirmationMessage);
        Assert.Contains("383 MB", empty[0].Description);
    }

    [Fact]
    public async Task DownloadAction_GatesOnLicenseThenDownloadsWithActivity()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = false };
        var host = new TestPluginHostServices();
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(host);
        var activity = new List<string?>();
        sut.SettingsActivityChanged += activity.Add;

        var blocked = await sut.ExecuteSettingsActionAsync(SupertonicTtsPlugin.DownloadActionId, CancellationToken.None);
        await sut.SetSettingValueAsync(SupertonicTtsPlugin.LicenseAcceptedSettingName, "true");
        var downloaded = await sut.ExecuteSettingsActionAsync(SupertonicTtsPlugin.DownloadActionId, CancellationToken.None);

        Assert.Equal(new PluginSettingsValidationResult(false, "Settings.AcceptLicense"), blocked);
        Assert.Equal(new PluginSettingsValidationResult(true, "Settings.DownloadComplete"), downloaded);
        Assert.Equal(1, assets.DownloadCount);
        Assert.True(sut.IsConfigured);
        Assert.Null(sut.SettingsProgress);
        Assert.Equal("Settings.Downloading", activity[0]);
        Assert.Null(activity[^1]);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task DownloadAction_ReportsFailureAndCancellationAndClearsActivity()
    {
        var assets = new FakeSupertonicAssets { DownloadFailure = new InvalidDataException("hash mismatch") };
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());
        sut.SetLicenseAccepted(true);

        var failed = await sut.ExecuteSettingsActionAsync(SupertonicTtsPlugin.DownloadActionId, CancellationToken.None);
        assets.DownloadFailure = null;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await sut.ExecuteSettingsActionAsync(SupertonicTtsPlugin.DownloadActionId, cts.Token);

        Assert.Equal(new PluginSettingsValidationResult(false, "Settings.Error: hash mismatch"), failed);
        Assert.Equal(new PluginSettingsValidationResult(false, "Settings.DownloadCancelled"), cancelled);
        Assert.False(sut.IsConfigured);
        Assert.Null(sut.SettingsProgress);
    }

    [Fact]
    public async Task ExecuteSettingsActionAsync_RejectsUnknownAction()
    {
        var sut = new SupertonicTtsPlugin(new FakeSupertonicAssets(), _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.ExecuteSettingsActionAsync("format-disk", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAction_WaitsForInFlightSynthesisThenUnloadsAndDeletes()
    {
        var gate = new TaskCompletionSource();
        var synth = new FakeSupertonicSynthesizer { Gate = gate.Task };
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true, HasAnyAssetsValue = true };
        var host = new TestPluginHostServices { PcmPlayback = new RecordingPcmPlaybackService() };
        var sut = new SupertonicTtsPlugin(assets, _ => synth);
        await sut.ActivateAsync(host);
        Task<PluginSettingsValidationResult>? remove = null;

        try
        {
            var speak = sut.SpeakAsync(new TtsSpeakRequest("Hello", "en"), CancellationToken.None);
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard.
            await synth.Started.WaitAsync(s_coordinationTimeout);

            remove = sut.ExecuteSettingsActionAsync(SupertonicTtsPlugin.RemoveActionId, CancellationToken.None);
            // ReSharper disable once MethodSupportsCancellation -- fixed settle window proving removal waits for synthesis.
            await Task.Delay(50);
            Assert.False(remove.IsCompleted);
            Assert.Equal(0, assets.RemoveCount);
            Assert.False(synth.Disposed);

            gate.SetResult();
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard.
            var session = await speak.WaitAsync(s_coordinationTimeout);
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard.
            var removed = await remove.WaitAsync(s_coordinationTimeout);

            Assert.True(session.IsActive);
            Assert.Equal(new PluginSettingsValidationResult(true, "Settings.Removed"), removed);
            Assert.True(synth.Disposed);
            Assert.Equal(1, assets.RemoveCount);
            Assert.False(sut.IsConfigured);
            Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        }
        finally
        {
            gate.TrySetResult();
            await CompleteBestEffort(remove);
        }
    }

    [Fact]
    public async Task RemoveAction_ReportsTheCapabilityChangeEvenWhenDeletingFails()
    {
        var assets = new FakeSupertonicAssets
        {
            AreAssetsReadyValue = true,
            RemoveFailure = new UnauthorizedAccessException("read-only"),
        };
        var host = new TestPluginHostServices();
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(host);

        var result = await sut.ExecuteSettingsActionAsync(SupertonicTtsPlugin.RemoveActionId, CancellationToken.None);

        Assert.Equal(new PluginSettingsValidationResult(false, "Settings.Error: read-only"), result);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SpeakAsync_RejectsAssetsThatFailVerificationBeforeLoading()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true };
        var host = new TestPluginHostServices { PcmPlayback = new RecordingPcmPlaybackService() };
        var factoryCalls = 0;
        var sut = new SupertonicTtsPlugin(assets, _ =>
        {
            factoryCalls++;
            return new FakeSupertonicSynthesizer();
        });
        await sut.ActivateAsync(host);
        assets.VerifyResult = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello", "en"), CancellationToken.None));

        Assert.Contains("failed verification", ex.Message);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SpeakAsync_RejectsSpeechLongerThanTwoMinutes()
    {
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true };
        var playback = new RecordingPcmPlaybackService();
        var synth = new FakeSupertonicSynthesizer { Samples = new float[24_000 * 120 + 1] };
        var sut = new SupertonicTtsPlugin(assets, _ => synth);
        await sut.ActivateAsync(new TestPluginHostServices { PcmPlayback = playback });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello", "en"), CancellationToken.None));

        Assert.Empty(playback.Requests);
    }

    [Fact]
    public void PreprocessText_WrapsTextInOpeningAndClosingLanguageTags()
    {
        // Matches Supertone's reference helper: f"<{lang}>" + text + f"</{lang}>".
        Assert.Equal("<en>Hello world.</en>", SupertonicTextProcessor.PreprocessText("Hello world", "EN"));
        Assert.Equal("<de>Hallo!</de>", SupertonicTextProcessor.PreprocessText("Hallo!", "de"));
    }

    [Fact]
    public void ChunkText_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 299) + "\U0001F600" + new string('b', 50);

        var chunks = SupertonicOnnxSynthesizer.ChunkText(text, 300);

        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk =>
        {
            Assert.False(char.IsHighSurrogate(chunk[^1]));
            Assert.False(char.IsLowSurrogate(chunk[0]));
        });
    }

    [Fact]
    public void NativeRuntime_LooksForThePublishedThenTheBuildLayout()
    {
        var candidates = SupertonicNativeRuntime.Candidates("/plugins/supertonic");

        Assert.Equal(Path.Join("/plugins/supertonic", "libonnxruntime.so"), candidates[0]);
        Assert.Matches(@"/plugins/supertonic/runtimes/linux-(x64|arm64)/native/libonnxruntime\.so$", candidates[1]);
    }

    [Fact]
    public void AudioLimits_AllowExactlyTwoMinutes()
    {
        var maximum = SupertonicAudioLimits.MaximumSamples(44_100);

        Assert.Equal(44_100L * 120, maximum);
        SupertonicAudioLimits.ValidateSampleCount(maximum, maximum);
        Assert.Throws<InvalidOperationException>(() => SupertonicAudioLimits.ValidateSampleCount(maximum + 1, maximum));
        Assert.Throws<InvalidOperationException>(() => SupertonicAudioLimits.ValidateSampleCount(double.NaN, maximum));
        Assert.Throws<InvalidOperationException>(() => SupertonicAudioLimits.MaximumSamples(0));
    }

    [Fact]
    public async Task GetSettingDefinitions_ExposesLicenseVoiceSpeedAndSteps()
    {
        var sut = new SupertonicTtsPlugin(new FakeSupertonicAssets(), _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());

        var definitions = sut.GetSettingDefinitions();
        var keys = definitions.Select(d => d.Key).ToArray();

        Assert.Equal(
            [
                SupertonicTtsPlugin.LicenseAcceptedSettingName,
                SupertonicTtsPlugin.SelectedVoiceSettingName,
                SupertonicTtsPlugin.SpeedSettingName,
                SupertonicTtsPlugin.DenoisingStepsSettingName,
            ],
            keys);

        var license = definitions.Single(d => d.Key == SupertonicTtsPlugin.LicenseAcceptedSettingName);
        Assert.Equal(PluginSettingKind.Boolean, license.Kind);

        var voice = definitions.Single(d => d.Key == SupertonicTtsPlugin.SelectedVoiceSettingName);
        Assert.NotNull(voice.Options);
        Assert.Equal(10, voice.Options.Count);
        Assert.Contains(voice.Options!, option => option.Value == "F3");
    }

    [Fact]
    public async Task SettingsRoundTrip_PersistsLicenseVoiceSpeedAndSteps()
    {
        var sut = new SupertonicTtsPlugin(new FakeSupertonicAssets(), _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());

        await sut.SetSettingValueAsync(SupertonicTtsPlugin.LicenseAcceptedSettingName, "true");
        await sut.SetSettingValueAsync(SupertonicTtsPlugin.SelectedVoiceSettingName, "F2");
        await sut.SetSettingValueAsync(SupertonicTtsPlugin.SpeedSettingName, "1.25");
        await sut.SetSettingValueAsync(SupertonicTtsPlugin.DenoisingStepsSettingName, "12");

        Assert.Equal("true", await sut.GetSettingValueAsync(SupertonicTtsPlugin.LicenseAcceptedSettingName));
        Assert.Equal("F2", await sut.GetSettingValueAsync(SupertonicTtsPlugin.SelectedVoiceSettingName));
        Assert.Equal("1.25", await sut.GetSettingValueAsync(SupertonicTtsPlugin.SpeedSettingName));
        Assert.Equal("12", await sut.GetSettingValueAsync(SupertonicTtsPlugin.DenoisingStepsSettingName));
        Assert.True(sut.HasAcceptedModelLicense);
        Assert.Equal("F2", sut.SelectedVoiceId);
        Assert.Equal(1.25, sut.Speed);
        Assert.Equal(12, sut.DenoisingSteps);
    }

    [Fact]
    public async Task DownloadAssetsAsync_SerializesConcurrentCallsToASingleDownload()
    {
        var gate = new TaskCompletionSource();
        var assets = new FakeSupertonicAssets { Gate = gate.Task };
        var sut = new SupertonicTtsPlugin(assets, _ => new FakeSupertonicSynthesizer());
        await sut.ActivateAsync(new TestPluginHostServices());
        sut.SetLicenseAccepted(true);

        var first = sut.DownloadAssetsAsync(null, CancellationToken.None);
        var second = sut.DownloadAssetsAsync(null, CancellationToken.None);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, assets.DownloadCount);
        Assert.True(sut.IsConfigured);
    }

    [Fact]
    public async Task DeactivateAsync_WaitsForInFlightSynthesisBeforeDisposingSynthesizer()
    {
        var gate = new TaskCompletionSource();
        var synth = new FakeSupertonicSynthesizer { Gate = gate.Task };
        var assets = new FakeSupertonicAssets { AreAssetsReadyValue = true };
        var sut = new SupertonicTtsPlugin(assets, _ => synth);
        await sut.ActivateAsync(new TestPluginHostServices
        {
            PcmPlayback = new RecordingPcmPlaybackService(),
        });
        using var speakCts = new CancellationTokenSource();
        Task<ITtsPlaybackSession>? speak = null;
        Task? deactivate = null;

        try
        {
            // ReSharper disable once MethodSupportsCancellation -- Task.Run must schedule unconditionally; speakCts.Token would cancel scheduling, not the synthesis under test.
            // ReSharper disable once AccessToDisposedClosure -- the task is awaited (speak.WaitAsync / CompleteBestEffort) before the using var speakCts is disposed at scope end.
            speak = Task.Run(() =>
                sut.SpeakAsync(new TtsSpeakRequest("Hello", "en"), speakCts.Token));
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; wiring a token here would make the deadline racy with the finally's teardown cancel instead of fixed.
            await synth.Started.WaitAsync(s_coordinationTimeout);

            deactivate = sut.DeactivateAsync();
            // ReSharper disable once MethodSupportsCancellation -- fixed settle window proving DeactivateAsync stays pending while synthesis is in flight; a token would defeat the check.
            await Task.Delay(50);
            Assert.False(deactivate.IsCompleted);
            Assert.False(synth.Disposed);

            gate.SetResult();
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; wiring a token here would make the deadline racy with the finally's teardown cancel instead of fixed.
            await speak.WaitAsync(s_coordinationTimeout);
            // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; wiring a token here would make the deadline racy with the finally's teardown cancel instead of fixed.
            await deactivate.WaitAsync(s_coordinationTimeout);

            Assert.True(synth.Disposed);
        }
        finally
        {
            gate.TrySetResult();
            // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel is the teardown signal; CancelAsync buys nothing in cleanup.
            speakCts.Cancel();
            await CompleteBestEffort(speak);
            deactivate ??= sut.DeactivateAsync();
            await CompleteBestEffort(deactivate);
        }
    }

    private static async Task CompleteBestEffort(params Task?[] tasks)
    {
        var activeTasks = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        if (activeTasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(activeTasks).WaitAsync(s_coordinationTimeout);
        }
        catch
        {
            // Best-effort bounded observation after releasing the gate and canceling speech.
        }
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Join(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Join(parts)}");
    }

    private sealed class FakeSupertonicAssets : ISupertonicAssetManager
    {
        public string AssetRoot { get; set; } = Path.GetTempPath();
        public long TotalSizeBytes => SupertonicAssetManager.DefaultFiles.Sum(file => file.SizeBytes);
        public bool AreAssetsReadyValue { get; set; }
        public bool HasAnyAssetsValue { get; set; }
        public bool? VerifyResult { get; set; }
        public Exception? DownloadFailure { get; set; }
        public Exception? RemoveFailure { get; init; }
        public int DownloadCount { get; private set; }
        public int VerifyCount { get; private set; }
        public int RemoveCount { get; private set; }
        public Task? Gate { get; set; }
        public bool AreAssetsReady => AreAssetsReadyValue;
        public bool HasAnyAssets => HasAnyAssetsValue || AreAssetsReadyValue;

        public Task<bool> VerifyCachedAssetsAsync(CancellationToken ct)
        {
            VerifyCount++;
            if (VerifyResult is { } result)
                AreAssetsReadyValue = result;
            return Task.FromResult(AreAssetsReadyValue);
        }

        public async Task DownloadMissingAssetsAsync(IProgress<double>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Gate is not null)
                await Gate;
            if (DownloadFailure is not null)
                throw DownloadFailure;
            DownloadCount++;
            AreAssetsReadyValue = true;
            progress?.Report(1.0);
        }

        public Task RemoveAssetsAsync(CancellationToken ct)
        {
            RemoveCount++;
            if (RemoveFailure is not null)
                throw RemoveFailure;
            AreAssetsReadyValue = false;
            HasAnyAssetsValue = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSupertonicSynthesizer : ISupertonicSynthesizer
    {
        private readonly TaskCompletionSource _started = new();
        public SupertonicSynthesisRequest? LastRequest { get; private set; }
        public Task? Gate { get; set; }
        public float[] Samples { get; set; } = [0.1f, -0.1f];
        public bool Disposed { get; private set; }
        public Task Started => _started.Task;

        public SupertonicSynthesisResult Synthesize(SupertonicSynthesisRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            _started.TrySetResult();
            Gate?.Wait(ct);
            return new SupertonicSynthesisResult(Samples, 24_000);
        }

        public void Dispose() => Disposed = true;
    }

    internal sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(s_jsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory { get; init; } = Path.GetTempPath();
        public IPluginPcmPlaybackService PcmPlayback { get; init; } =
            UnavailablePluginPcmPlaybackService.Instance;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        // Keeps the arguments visible so tests can see what a formatted message carried.
        public string GetString(string key, params object[] args) => $"{key}: {string.Join(" | ", args)}";
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
