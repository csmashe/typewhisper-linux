using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LanguageHintDefaultMethodTests
{
    [Fact]
    public async Task TranscribeWithLanguageHints_DefaultsToFirstHint()
    {
        var fake = new FakeRole();
        await ((ITranscriptionEngineRole)fake).TranscribeWithLanguageHintsAsync([], ["de", "en"], false, null, CancellationToken.None);
        Assert.Equal("de", fake.LastLanguage);
    }

    [Fact]
    public async Task TranscribeStreamingWithLanguageHints_DefaultsToFirstHint()
    {
        var fake = new FakeRole();
        await ((ITranscriptionEngineRole)fake).TranscribeStreamingWithLanguageHintsAsync([], ["de", "en"], false, null, _ => true, CancellationToken.None);
        Assert.Equal("de", fake.LastLanguage);
    }

    [Fact]
    public async Task StartStreamingWithLanguageHints_DefaultsToFirstHint()
    {
        var fake = new FakeRole();
        await using var session = await ((ITranscriptionEngineRole)fake).StartStreamingWithLanguageHintsAsync(["de", "en"], CancellationToken.None);
        Assert.Equal("de", fake.LastLanguage);
    }

    [Fact]
    public void SupportsLanguageHints_DefaultsToFalse() => Assert.False(((ITranscriptionEngineRole)new FakeRole()).SupportsLanguageHints);

    [Fact]
    public async Task FirstHint_SkipsBlankEntries()
    {
        var fake = new FakeRole();
        await ((ITranscriptionEngineRole)fake).TranscribeWithLanguageHintsAsync([], ["", " de ", "en"], false, null, CancellationToken.None);
        Assert.Equal("de", fake.LastLanguage);
    }

    private sealed class FakeRole : ITranscriptionEngineRole
    {
        public string PluginId => "test";
        public string ProviderId => "test";
        public string ProviderDisplayName => "Test";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string SelectedModelId => "test";
        public bool SupportsTranslation => false;
        public string? LastLanguage { get; private set; }
        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
        {
            LastLanguage = language;
            return Task.FromResult(new PluginTranscriptionResult("text", language, 0, null));
        }

        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
        {
            LastLanguage = language;
            return Task.FromResult<IStreamingSession>(new StubSession());
        }
    }

    private sealed class StubSession : IStreamingSession
    {
        public event Action<StreamingTranscriptEvent> TranscriptReceived { add { } remove { } }
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
