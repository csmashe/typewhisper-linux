using System.Text;
using TypeWhisper.Core.Audio;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    private const string EchoPrompt = "Kwyjibo, Hillger, seofood, Zelnorm, Quixxa, Brontolux";

    [Theory]
    [InlineData("Fachbegriff{0:00}")]
    [InlineData("東京都庁{0:00}")]
    [InlineData("a3f9C2e1x{0:00}")]
    public void DictionaryTermsStayWholeWithinWhispersPromptWindow(string pattern)
    {
        using var plugin = new WhisperCppPlugin();
        ITranscriptionEngineRole role = plugin;
        Assert.True(role.SupportsStructuredDictionaryTerms);
        var terms = Enumerable.Range(0, 100).Select(index => string.Format(pattern, index)).ToArray();

        var prompt = WhisperPrompt.Create(
            PluginTranscriptionPrompt.Encode(null, PluginDictionaryTerms.Clip(terms, role.DictionaryTermsBudget)))!;

        // Every token of Whisper's byte-level tokenizer covers at least one UTF-8 byte.
        Assert.InRange(Encoding.UTF8.GetByteCount(prompt.Value), WhisperPrompt.MaxBytes - 30, WhisperPrompt.MaxBytes);
        var kept = prompt.Value.Split(", ");
        Assert.Equal(terms.Take(kept.Length), kept);
        Assert.True(prompt.HasTerms);
        Assert.Null(prompt.Text);
    }

    [Fact]
    public void StructuredTermsKeepTheirCommas()
    {
        var prompt = WhisperPrompt.Create(PluginTranscriptionPrompt.Encode(null, ["Grüße, Marco", "TypeWhisper"]));

        Assert.Equal("Grüße, Marco, TypeWhisper", prompt?.Value);
        Assert.Null(WhisperPrompt.Create(null));
        Assert.Null(WhisperPrompt.Create(" "));
    }

    [Fact]
    public void CallerTextComesFirstAndTermsFillTheRestOfTheWindow()
    {
        var terms = Enumerable.Range(0, 100).Select(index => $"Fachbegriff{index:00}").ToArray();

        var prompt = WhisperPrompt.Create(PluginTranscriptionPrompt.Encode("Meeting notes.", terms))!;

        Assert.StartsWith("Meeting notes.\nFachbegriff00, Fachbegriff01", prompt.Value);
        Assert.InRange(Encoding.UTF8.GetByteCount(prompt.Value), WhisperPrompt.MaxBytes - 30, WhisperPrompt.MaxBytes);
        Assert.Equal("Meeting notes.", prompt.Text);
        Assert.True(prompt.HasTerms);
    }

    [Fact]
    public void CallerTextLongerThanTheWindowIsKeptWithoutTerms()
    {
        var text = new string('a', WhisperPrompt.MaxBytes + 50);

        var prompt = WhisperPrompt.Create(PluginTranscriptionPrompt.Encode(text, ["TypeWhisper"]))!;

        Assert.Equal(text, prompt.Value);
        Assert.False(prompt.HasTerms);
        Assert.Equal(text, WhisperPrompt.Create(text)?.Value);
    }

    // Transcripts large-v3-turbo returned for noise and speech with this prompt (upstream #617).
    [Theory]
    [InlineData("Kwek, Hillger, seofood, Kwek", true)]
    [InlineData("Kwixta", true)]
    [InlineData("Kwyjibo", true)]
    [InlineData("hillger.", true)]
    [InlineData("Please send the Quijibo build to Marko Hillger at seofood.", false)]
    [InlineData("Hillger hat angerufen", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    // tiny.en returned these for noise and silence with this prompt, and nothing without it.
    [InlineData("[SOUND]", true)]
    [InlineData("[BLANK_AUDIO]", true)]
    [InlineData("[BLANK_AUDIO] Hillger", true)]
    [InlineData("[MUSIC] Please send the build.", false)]
    public void PromptEchoIsSuspectedOnlyWhenEveryWordResemblesATerm(string text, bool suspected) =>
        Assert.Equal(suspected, DictionaryPromptEcho.IsSuspected(text, EchoPrompt));

    [Theory]
    [InlineData(".", false)]
    [InlineData(" ... ", false)]
    [InlineData(null, false)]
    [InlineData("[SOUND]", false)]
    [InlineData("(wind blowing)", false)]
    [InlineData("Hilger", true)]
    [InlineData("東京", true)]
    public void PunctuationAloneHasNoWords(string? text, bool words) =>
        Assert.Equal(words, DictionaryPromptEcho.HasWords(text));

    [Fact]
    public async Task EchoedTermsOverSilenceAreDiscarded()
    {
        var (decode, prompts) = FakeDecoder(withPrompt: "Kwek, Hillger", withoutPrompt: "");
        var logs = new List<string>();

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode(null, EchoPrompt.Split(", ")), logs.Add);

        Assert.Equal("", result.Text);
        Assert.Equal([EchoPrompt, null], prompts);
        Assert.Single(logs);
    }

    [Fact]
    public async Task ANonSpeechMarkerThePromptLetThroughIsDiscarded()
    {
        var (decode, prompts) = FakeDecoder(withPrompt: "[BLANK_AUDIO]", withoutPrompt: "");

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode(null, EchoPrompt.Split(", ")), _ => { });

        Assert.Equal("", result.Text);
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    public async Task SpeechRecoveredFromAMarkerOnlyResultIsKept()
    {
        var (decode, _) = FakeDecoder(withPrompt: "[BLANK_AUDIO]", withoutPrompt: "Send it now.");
        var logs = new List<string>();

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode(null, EchoPrompt.Split(", ")), logs.Add);

        Assert.Equal("Send it now.", result.Text);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task ADictatedTermAloneKeepsThePromptedSpelling()
    {
        var (decode, prompts) = FakeDecoder(withPrompt: "Kwyjibo", withoutPrompt: "Quick shibo");

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode(null, EchoPrompt.Split(", ")), _ => { });

        Assert.Equal("Kwyjibo", result.Text);
        Assert.Equal(2, prompts.Count);
    }

    [Fact]
    public async Task AKeptSpellingCarriesThePlainPassSpeechConfidence()
    {
        var (decode, _) = FakeDecoder(
            withPrompt: "Thank you", withoutPrompt: "Thank you.", withoutPromptNoSpeechProbability: 0.1f, withPromptNoSpeechProbability: 0.5f);

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode(null, ["Thank you"]), _ => { });

        Assert.Equal("Thank you", result.Text);
        Assert.Equal(0.1f, result.NoSpeechProbability);
    }

    [Fact]
    public async Task AnEchoBackedOnlyByUnsureWordsReturnsThePlainResult()
    {
        var (decode, _) = FakeDecoder(withPrompt: "Hillger", withoutPrompt: "Thank you.", withoutPromptNoSpeechProbability: 0.5f);

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode(null, EchoPrompt.Split(", ")), _ => { });

        Assert.Equal("Thank you.", result.Text);
        Assert.Equal(0.5f, result.NoSpeechProbability);
    }

    [Fact]
    public async Task SpeechThatDiffersFromThePromptDecodesOnce()
    {
        var (decode, prompts) = FakeDecoder(withPrompt: "Please send the Kwyjibo build.", withoutPrompt: "unused");

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode(null, EchoPrompt.Split(", ")), _ => { });

        Assert.Equal("Please send the Kwyjibo build.", result.Text);
        Assert.Equal([EchoPrompt], prompts);
    }

    [Fact]
    public async Task TheSecondDecodeKeepsTheCallersPromptText()
    {
        var (decode, prompts) = FakeDecoder(withPrompt: "Hillger", withoutPrompt: "");

        await WhisperCppPlugin.TranscribeWithPromptAsync(
            decode, PluginTranscriptionPrompt.Encode("Notes.", ["Hillger"]), _ => { });

        Assert.Equal(["Notes.\nHillger", "Notes."], prompts);
    }

    [Fact]
    public async Task PromptTextWithoutTermsNeverDecodesTwice()
    {
        var (decode, prompts) = FakeDecoder(withPrompt: "unused", withoutPrompt: "Notes");

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(decode, "Notes.", _ => { });

        Assert.Equal("Notes", result.Text);
        Assert.Equal(["Notes."], prompts);
    }

    [Fact]
    public async Task NoPromptDecodesOnceWithoutOne()
    {
        var (decode, prompts) = FakeDecoder(withPrompt: "unused", withoutPrompt: "Hello");

        var result = await WhisperCppPlugin.TranscribeWithPromptAsync(decode, null, _ => { });

        Assert.Equal("Hello", result.Text);
        Assert.Equal([null], prompts);
    }

    // Opt-in: set TYPEWHISPER_TEST_WHISPER_ASSETS to a folder containing Models/ggml-tiny.en.bin.
    [Fact]
    public async Task SilenceAndNoiseWithADictionaryPromptStayEmpty()
    {
        var assets = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_WHISPER_ASSETS");
        if (string.IsNullOrWhiteSpace(assets))
            return;

        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(CreateHostMock(assets).Object);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        await plugin.LoadModelAsync("tiny.en", null, CancellationToken.None);
        var prompt = PluginTranscriptionPrompt.Encode(null, EchoPrompt.Split(", "));
        var random = new Random(1);
        float[][] clips =
        [
            new float[16_000 * 3],
            Enumerable.Range(0, 16_000 * 3).Select(_ => (float)(random.NextDouble() - .5) * .06f).ToArray(),
        ];

        foreach (var clip in clips)
        {
            var result = await plugin.TranscribeAsync(WavEncoder.Encode(clip), "en", false, prompt, CancellationToken.None);
            Assert.False(DictionaryPromptEcho.HasWords(result.Text), result.Text);
            Assert.False(DictionaryPromptEcho.IsSuspected(result.Text, EchoPrompt), result.Text);
        }
    }

    private static (Func<string?, Task<PluginTranscriptionResult>> Decode, List<string?> Prompts) FakeDecoder(
        string withPrompt,
        string withoutPrompt,
        float? withoutPromptNoSpeechProbability = 0.1f,
        float? withPromptNoSpeechProbability = 0f)
    {
        var prompts = new List<string?>();
        return (prompt =>
        {
            prompts.Add(prompt);
            var hasTerms = prompt is not null && prompt != "Notes.";
            return Task.FromResult(hasTerms
                ? new PluginTranscriptionResult(withPrompt, "en", 1, withPromptNoSpeechProbability)
                : new PluginTranscriptionResult(withoutPrompt, "en", 1, withoutPromptNoSpeechProbability));
        }, prompts);
    }
}
