using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TranscriptionPromptComposerTests
{
    private static readonly string[] s_terms = ["Washington, D.C.", "Grüße", "too many words here"];

    [Fact]
    public void LegacyEngine_GetsNoDictationPrompt() =>
        Assert.Null(TranscriptionPromptComposer.ForDictation(new Engine(structured: false), s_terms));

    [Fact]
    public void LegacyEngine_ApiPromptIsUnchanged()
    {
        var engine = new Engine(structured: false) { Budget = new DictionaryTermsBudget(MaxWordsPerTerm: 2) };

        Assert.Equal(
            "Spell it out" + Environment.NewLine + "Washington, D.C., Grüße",
            TranscriptionPromptComposer.ForApi(engine, "Spell it out", s_terms));
        Assert.Equal("Washington, D.C., Grüße", TranscriptionPromptComposer.ForApi(engine, null, s_terms));
        Assert.Null(TranscriptionPromptComposer.ForApi(engine, " ", []));
    }

    [Fact]
    public void LegacyEngine_WithoutBudget_UsesTheSdkDefault()
    {
        var terms = Enumerable.Range(0, 200).Select(i => $"term{i:D3}").ToList();

        var prompt = TranscriptionPromptComposer.ForApi(new Engine(structured: false), null, terms);

        Assert.Equal(PluginDictionaryTerms.CreatePrompt(terms), prompt);
        Assert.True(prompt!.Length <= DictionaryTermsBudget.Default.MaxTotalChars);
    }

    [Fact]
    public void StructuredEngine_DictationGetsClippedTermsWithoutText()
    {
        var engine = new Engine(structured: true) { Budget = new DictionaryTermsBudget(MaxWordsPerTerm: 2) };

        var parsed = PluginTranscriptionPrompt.Parse(TranscriptionPromptComposer.ForDictation(engine, s_terms));

        Assert.Null(parsed.Text);
        Assert.Equal(["Washington, D.C.", "Grüße"], parsed.DictionaryTerms);
    }

    [Fact]
    public void StructuredEngine_ApiKeepsTextApartFromTerms()
    {
        var engine = new Engine(structured: true) { Budget = new DictionaryTermsBudget(MaxWordsPerTerm: 2) };
        var text = TranscriptionPromptComposer.MergeText("Alpha, Beta", "Likely spoken languages: de, en.");

        var parsed = PluginTranscriptionPrompt.Parse(TranscriptionPromptComposer.ForApi(engine, text, s_terms));

        Assert.Equal("Alpha, Beta" + Environment.NewLine + "Likely spoken languages: de, en.", parsed.Text);
        Assert.Equal(["Washington, D.C.", "Grüße"], parsed.DictionaryTerms);
    }

    [Fact]
    public void StructuredEngine_BudgetCountsTermsNotJsonEscaping()
    {
        var engine = new Engine(structured: true) { Budget = new DictionaryTermsBudget(MaxTotalChars: 12) };

        var parsed = PluginTranscriptionPrompt.Parse(
            TranscriptionPromptComposer.ForDictation(engine, ["\"a\"\\b", "Öl", "dropped"]));

        Assert.Equal(["\"a\"\\b", "Öl"], parsed.DictionaryTerms);
    }

    [Fact]
    public void StructuredEngine_WithNothingToSend_GetsNoPrompt()
    {
        var engine = new Engine(structured: true);

        Assert.Null(TranscriptionPromptComposer.ForDictation(engine, []));
        Assert.Null(TranscriptionPromptComposer.ForApi(engine, null, [" "]));
    }

    [Fact]
    public async Task StreamingWithoutPrompt_OpensTheSessionAsBefore()
    {
        var engine = new Engine(structured: true);

        await engine.StartStreamingAsync(LanguageSelection.Automatic, ["de", "en"], null, CancellationToken.None);
        await engine.StartStreamingAsync(LanguageSelection.Explicit("fr"), [], null, CancellationToken.None);

        Assert.Equal(["hints:de,en", "single:fr"], engine.StreamingCalls);
    }

    [Fact]
    public async Task StreamingWithPrompt_CarriesItWithResolvedHints()
    {
        var engine = new Engine(structured: true);
        var prompt = TranscriptionPromptComposer.ForDictation(engine, s_terms);

        await engine.StartStreamingAsync(LanguageSelection.Automatic, ["de", "en"], prompt, CancellationToken.None);
        await engine.StartStreamingAsync(LanguageSelection.Explicit("fr"), [], prompt, CancellationToken.None);
        await engine.StartStreamingAsync(LanguageSelection.Automatic, [], prompt, CancellationToken.None);

        Assert.Equal(["prompt:de,en", "prompt:fr", "prompt:"], engine.StreamingCalls);
        Assert.All(engine.StreamingPrompts, received => Assert.Same(prompt, received));
    }

    private sealed class Engine(bool structured) : ITranscriptionEngineRole
    {
        public List<string> StreamingCalls { get; } = [];
        public List<string?> StreamingPrompts { get; } = [];
        public DictionaryTermsBudget? Budget { get; init; }

        public string PluginId => "test-prompt";
        public string ProviderId => "test-prompt";
        public string ProviderDisplayName => "Test prompt";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("test", "Test")];
        public string SelectedModelId => "test";
        public bool SupportsTranslation => false;
        public bool SupportsStreaming => true;
        public bool SupportsLanguageHints => true;
        public bool SupportsStructuredDictionaryTerms => structured;
        public DictionaryTermsBudget? DictionaryTermsBudget => Budget;

        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
        {
            StreamingCalls.Add($"single:{language}");
            return Task.FromResult<IStreamingSession>(null!);
        }

        public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
            IReadOnlyList<string> languageHints, CancellationToken ct)
        {
            StreamingCalls.Add($"hints:{string.Join(',', languageHints)}");
            return Task.FromResult<IStreamingSession>(null!);
        }

        public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
            IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct)
        {
            StreamingCalls.Add($"prompt:{string.Join(',', languageHints)}");
            StreamingPrompts.Add(prompt);
            return Task.FromResult<IStreamingSession>(null!);
        }
    }
}
