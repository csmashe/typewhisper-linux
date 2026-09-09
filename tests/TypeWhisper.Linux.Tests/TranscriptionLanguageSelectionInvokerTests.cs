using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TranscriptionLanguageSelectionInvokerTests
{
    [Fact]
    public async Task TranscribeAsync_MultipleHints_CallsHintsOverloadWithCanonicalDistinctList()
    {
        var role = new FakeRole { SupportedLanguages = ["de-DE", "en"] };
        await role.TranscribeAsync([], LanguageSelection.Explicit("de-DE"), ["de-de", "en", "DE", "xx!!"], false, null, CancellationToken.None);
        Assert.Equal(["de-DE", "en"], role.LastHints);
        Assert.Equal(0, role.SingleCalls);
    }

    [Fact]
    public async Task TranscribeAsync_SingleHint_UsesSingleLanguageCall()
    {
        var role = new FakeRole();
        await role.TranscribeAsync([], LanguageSelection.Explicit("de"), ["de"], false, null, CancellationToken.None);
        Assert.Equal("de", role.LastLanguage);
        Assert.Equal(1, role.SingleCalls);
        Assert.Null(role.LastHints);
    }

    [Fact]
    public async Task TranscribeAsync_AutomaticPrimary_DropsHintsForEnginesWithoutNativeSupport()
    {
        var role = new FakeRole();
        await role.TranscribeAsync([], LanguageSelection.Automatic, ["de", "en"], false, null, CancellationToken.None);
        Assert.Equal(1, role.SingleCalls);
        Assert.Null(role.LastLanguage);
        Assert.Null(role.LastHints);
    }

    [Theory]
    [InlineData(new[] { "de", "en" }, new[] { "de", "en" })]
    [InlineData(new[] { "de" }, new[] { "de" })]
    public async Task TranscribeAsync_AutomaticPrimary_PassesAdvisoryHintsToNativeEngines(string[] hints, string[] expected)
    {
        var role = new FakeRole { SupportsLanguageHints = true };
        await role.TranscribeAsync([], LanguageSelection.Automatic, hints, false, null, CancellationToken.None);
        Assert.Equal(expected, role.LastHints);
        Assert.Equal(0, role.SingleCalls);
    }

    [Fact]
    public async Task TranscribeAsync_UnsupportedExtraHint_IsDroppedWhilePrimaryStaysStrict()
    {
        var role = new FakeRole { SupportedLanguages = ["de", "en"] };
        await role.TranscribeAsync([], LanguageSelection.Explicit("de"), ["de", "fr", "en"], false, null, CancellationToken.None);
        Assert.Equal(["de", "en"], role.LastHints);
        await Assert.ThrowsAsync<TranscriptionLanguageNotSupportedException>(() =>
            role.TranscribeAsync([], LanguageSelection.Explicit("fr"), ["de", "en"], false, null, CancellationToken.None));
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
        public IReadOnlyList<string> SupportedLanguages { get; init; } = [];
        public bool SupportsLanguageHints { get; init; }
        public IReadOnlyList<string>? LastHints { get; private set; }
        public int SingleCalls { get; private set; }
        public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
            byte[] wavAudio, IReadOnlyList<string> languageHints, bool translate, string? prompt, CancellationToken ct)
        {
            LastHints = languageHints;
            return Task.FromResult(new PluginTranscriptionResult("text", languageHints[0], 0));
        }

        public string? LastLanguage { get; private set; }
        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
        {
            SingleCalls++;
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
