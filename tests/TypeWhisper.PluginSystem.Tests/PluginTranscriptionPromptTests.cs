using TypeWhisper.Plugin.Meta;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginTranscriptionPromptTests
{
    private static readonly string[] s_terms =
        ["Washington, D.C.", "Grüße; Öl", "a\nb", "quoted \"name\"", "back\\slash", "東京", "🦀 Rust"];

    [Fact]
    public void TermsRoundTripWithPunctuationUnicodeAndOrder()
    {
        var prompt = PluginTranscriptionPrompt.Encode(null, [.. s_terms, "WASHINGTON, d.c.", "  ", " Öl "]);

        Assert.StartsWith(PluginTranscriptionPrompt.EnvelopePrefix, prompt);
        var parsed = PluginTranscriptionPrompt.Parse(prompt);
        Assert.Null(parsed.Text);
        Assert.Equal([.. s_terms, "Öl"], parsed.DictionaryTerms);
    }

    [Fact]
    public void TextStaysSeparateFromTerms()
    {
        var parsed = PluginTranscriptionPrompt.Parse(
            PluginTranscriptionPrompt.Encode("  Alpha, Beta\nLikely spoken languages: de, en.  ", ["Washington, D.C."]));

        Assert.Equal("Alpha, Beta\nLikely spoken languages: de, en.", parsed.Text);
        Assert.Equal(["Washington, D.C."], parsed.DictionaryTerms);
        Assert.Equal(
            "Alpha, Beta\nLikely spoken languages: de, en." + Environment.NewLine + "Washington, D.C.",
            parsed.ToPlainText());
    }

    [Fact]
    public void EmptyPromptsEncodeToNothing()
    {
        Assert.Null(PluginTranscriptionPrompt.Encode(null, null));
        Assert.Null(PluginTranscriptionPrompt.Encode(" ", [" ", ""]));
        Assert.Equal(["only"], PluginTranscriptionPrompt.Parse(PluginTranscriptionPrompt.Encode(null, ["only"])).DictionaryTerms);
        Assert.Equal("only", PluginTranscriptionPrompt.Parse(PluginTranscriptionPrompt.Encode("only", [])).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPromptParsesAsEmpty(string? prompt)
    {
        var parsed = PluginTranscriptionPrompt.Parse(prompt);
        Assert.Null(parsed.Text);
        Assert.Empty(parsed.DictionaryTerms);
        Assert.Null(parsed.ToPlainText());
    }

    [Theory]
    [InlineData("Alpha, Beta")]
    [InlineData("TypeWhisper.TranscriptionPrompt/2\n{\"dictionaryTerms\":[\"x\"]}")]
    [InlineData(" TypeWhisper.TranscriptionPrompt/1\n{\"dictionaryTerms\":[\"x\"]}")]
    public void AnythingButTheEnvelopeIsPlainText(string prompt)
    {
        var parsed = PluginTranscriptionPrompt.Parse(prompt);
        Assert.Equal(prompt.Trim(), parsed.Text);
        Assert.Empty(parsed.DictionaryTerms);
        Assert.Equal(prompt.Trim(), parsed.ToPlainText());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"terms\"")]
    [InlineData("{}")]
    [InlineData("{\"dictionaryTerms\":null}")]
    [InlineData("{\"dictionaryTerms\":\"Alpha, Beta\"}")]
    [InlineData("{\"dictionaryTerms\":[123]}")]
    [InlineData("{\"dictionaryTerms\":[null]}")]
    [InlineData("{\"text\":42,\"dictionaryTerms\":[]}")]
    [InlineData("{\"dictionaryTerms\":[\"x\"")]
    [InlineData("{\"dictionaryTerms\":[\"x\"]} trailing")]
    public void MalformedEnvelopeIsRejected(string json) =>
        Assert.Throws<FormatException>(() => PluginTranscriptionPrompt.Parse(PluginTranscriptionPrompt.EnvelopePrefix + json));

    [Fact]
    public void UnknownEnvelopePropertiesAreIgnored()
    {
        var parsed = PluginTranscriptionPrompt.Parse(
            PluginTranscriptionPrompt.EnvelopePrefix + "{\"text\":null,\"future\":1,\"dictionaryTerms\":[\"x\"]}");
        Assert.Null(parsed.Text);
        Assert.Equal(["x"], parsed.DictionaryTerms);
    }

    [Fact]
    public void EnginesDoNotOptInByDefault() =>
        Assert.False(((ITranscriptionEngineRole)new LegacyEngine()).SupportsStructuredDictionaryTerms);

    [Fact]
    public void MetaKeepsEachDictionaryTermWhole()
    {
        using var meta = new MetaPlugin();
        Assert.True(meta.SupportsStructuredDictionaryTerms);
        Assert.Equal(
            ["Washington, D.C.", "Grüße; Öl", "quoted \"name\""],
            MetaPlugin.ParseKeywords(PluginTranscriptionPrompt.Encode(null, ["Washington, D.C.", "Grüße; Öl", "quoted \"name\""])));
    }

    [Fact]
    public void MetaPromptTextKeepsItsCommaSeparatedKeywordsBeforeTerms() =>
        Assert.Equal(
            ["Alpha", "beta", "Washington, D.C."],
            MetaPlugin.ParseKeywords(PluginTranscriptionPrompt.Encode("Alpha, beta, Beta", ["alpha", "Washington, D.C."])));

    [Fact]
    public void MetaDropsATermThatNoLongerFitsAfterPromptKeywords()
    {
        // 6 x 98 + 2 = 590 of the 600 characters go to the prompt's keywords.
        var promptText = string.Join(", ", Enumerable.Range(0, 6).Select(i => new string((char)('a' + i), 98)).Append("zz"));
        string[] terms = ["Washington, D.C.", "one two three four five six seven eight nine", "Öl"];

        var keywords = MetaPlugin.ParseKeywords(PluginTranscriptionPrompt.Encode(promptText, terms));

        Assert.Equal(8, keywords.Count);
        Assert.Equal(["zz", "Öl"], keywords.TakeLast(2));
        Assert.Equal(592, keywords.Sum(keyword => keyword.Length));
    }

    [Fact]
    public void MetaPlainPromptStillSplitsOnCommas() =>
        Assert.Equal(["Washington", "D.C."], MetaPlugin.ParseKeywords("Washington, D.C."));

    [Fact]
    public void MetaBudgetDropsTermsItWouldOtherwiseTruncate()
    {
        using var meta = new MetaPlugin();
        var budget = meta.DictionaryTermsBudget;
        string[] terms = ["one two three four five six seven eight nine", new('a', 101), "kept"];

        var clipped = PluginDictionaryTerms.Clip(terms, budget);

        Assert.Equal(["kept"], clipped);
        Assert.Equal(["kept"], MetaPlugin.ParseKeywords(PluginTranscriptionPrompt.Encode(null, clipped)));
        Assert.Equal(100, PluginDictionaryTerms.Clip(Enumerable.Range(0, 101).Select(i => $"t{i}"), budget).Count);
        Assert.True(PluginDictionaryTerms.Clip(Enumerable.Range(0, 10).Select(i => i + new string('a', 99)), budget)
            .Sum(term => term.Length + 2) <= 602);
    }

    private sealed class LegacyEngine : ITranscriptionEngineRole
    {
        public string PluginId => "legacy";
        public string ProviderId => "legacy";
        public string ProviderDisplayName => "Legacy";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string? SelectedModelId => null;
        public bool SupportsTranslation => false;
        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
