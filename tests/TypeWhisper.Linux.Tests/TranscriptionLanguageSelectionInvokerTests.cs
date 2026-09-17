using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TranscriptionLanguageSelectionInvokerTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(12)]
    [InlineData(3)]
    public void LanguageNotSupportedMessage_ListsAtMostTwelveLanguages(int languageCount)
    {
        var codes = Enumerable.Range(1, languageCount).Select(i => $"en-x-{i:00}").ToArray();
        var exception = new TranscriptionLanguageNotSupportedException(
            "test", "test", LanguageSelection.Explicit("xx"), codes);

        var message = LanguageSelectionUiMessage.From(exception);

        Assert.Contains(string.Join(", ", codes.Take(12)), message);
        if (languageCount > 12)
        {
            Assert.Contains(", …", message);
            Assert.DoesNotContain(codes[12], message);
        }
        else
        {
            Assert.DoesNotContain("…", message);
        }

        Assert.Contains(string.Join(", ", codes), exception.Message);
    }

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

    [Fact]
    public async Task TranscribeAsync_BaseOnlyLanguages_FoldsRegionalPrimaryToSingleLanguage()
    {
        var role = new FakeRole { SupportedLanguages = ["de", "en"] };
        await role.TranscribeAsync([], LanguageSelection.Explicit("de-DE"), ["de-DE"], false, null, CancellationToken.None);
        Assert.Equal("de", role.LastLanguage);
        Assert.Equal(1, role.SingleCalls);
        Assert.Null(role.LastHints);
    }

    [Fact]
    public async Task TranscribeAsync_BaseOnlyLanguages_FoldsAndDeduplicatesHints()
    {
        var role = new FakeRole { SupportedLanguages = ["de", "en"] };
        await role.TranscribeAsync([], LanguageSelection.Explicit("de-DE"), ["de-DE", "en-GB", "de"], false, null, CancellationToken.None);
        Assert.Equal(["de", "en"], role.LastHints);
        Assert.Equal(0, role.SingleCalls);
    }

    [Fact]
    public async Task TranscribeAsync_MixedLanguages_RejectsUnsupportedRegionalPrimary()
    {
        var role = new FakeRole { SupportedLanguages = ["de", "de-CH", "en", "en-US"] };
        var exception = await Assert.ThrowsAsync<TranscriptionLanguageNotSupportedException>(() =>
            role.TranscribeAsync([], LanguageSelection.Explicit("de-DE"), false, null, CancellationToken.None));
        Assert.Equal(
            "Transcription provider 'test' model 'test' does not support language 'de-DE'. Supported languages: de, de-CH, en, en-US.",
            exception.Message);
        Assert.Equal(0, role.SingleCalls);
    }

    [Fact]
    public async Task TranscribeAsync_MixedLanguages_PreservesSupportedRegionalPrimary()
    {
        var role = new FakeRole { SupportedLanguages = ["de", "de-CH"] };
        await role.TranscribeAsync([], LanguageSelection.Explicit("de-CH"), false, null, CancellationToken.None);
        Assert.Equal("de-CH", role.LastLanguage);
        Assert.Equal(1, role.SingleCalls);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyLanguages_PreservesRegionalPrimary()
    {
        var role = new FakeRole();
        await role.TranscribeAsync([], LanguageSelection.Explicit("de-DE"), false, null, CancellationToken.None);
        Assert.Equal("de-DE", role.LastLanguage);
        Assert.Equal(1, role.SingleCalls);
    }

    [Fact]
    public async Task TranscribeAsync_BaseOnlyLanguages_FoldsRegionalExtraHint()
    {
        var role = new FakeRole { SupportedLanguages = ["de", "fr"] };
        await role.TranscribeAsync([], LanguageSelection.Explicit("de"), ["fr-FR"], false, null, CancellationToken.None);
        Assert.Equal(["de", "fr"], role.LastHints);
        Assert.Equal(0, role.SingleCalls);
    }

    [Theory]
    [InlineData(new[] { "de-ch" }, "de-CH", "de-CH")]
    [InlineData(new[] { "de" }, "de-DE", "de")]
    [InlineData(new[] { "de", "en-US" }, "de-DE", null)]
    [InlineData(new[] { "en" }, "de-DE", null)]
    [InlineData(new string[] { }, "xx-YY", "xx-YY")]
    public void ResolveSupportedTag_RespectsProviderList(string[] supportedLanguages, string languageTag, string? expected)
    {
        Assert.Equal(expected, TranscriptionLanguageSelectionInvoker.ResolveSupportedTag(supportedLanguages, languageTag));
    }

    [Fact]
    public void ResolveSupportedTag_NullList_AcceptsAnyTag()
    {
        Assert.Equal("de-DE", TranscriptionLanguageSelectionInvoker.ResolveSupportedTag(null, "de-DE"));
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
