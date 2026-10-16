using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="CleanupService" /> light cleanup (fillers, spoken punctuation/lists, backtrack corrections) and LLM prompt selection.</summary>
public sealed class CleanupServiceTests
{
    private readonly CleanupService _sut = new();

    [Theory]
    [InlineData("um uh send it tomorrow", "en-US", "Send it tomorrow")]
    [InlineData("um momento", "pt", "Um momento")]
    [InlineData("er ist hier", "de", "Er ist hier")]
    [InlineData("do you know the address", "en", "Do you know the address")]
    [InlineData("uh-huh that works", "en", "Uh-huh that works")]
    [InlineData("uh huh that works", "en", "Uh huh that works")]
    [InlineData("uh uh do not send", "en", "Uh uh do not send")]
    [InlineData("uh oh that failed", "en", "Uh oh that failed")]
    [InlineData("the ER and UM teams", "en", "The ER and UM teams")]
    [InlineData("write `um` and \"uh\" literally", "en", "Write `um` and \"uh\" literally")]
    [InlineData("well I like it actually", "en", "Well I like it actually")]
    public void Clean_Light_UsesLanguageAndPreservesMeaning(string input, string language, string expected)
    {
        Assert.Equal(expected, _sut.Clean(input, CleanupLevel.Light, language));
    }

    [Fact]
    public void Clean_None_ReturnsOriginalText()
    {
        const string text = "um hello   world";

        var result = _sut.Clean(text, CleanupLevel.None);

        Assert.Equal(text, result);
    }

    [Theory]
    [InlineData("um hello world", "Um hello world")]
    [InlineData("uh, hello world", "Hello world")]
    [InlineData("hello, you know, world", "Hello, world")]
    [InlineData("er hello ah world", "Er hello world")]
    [InlineData("err hello world", "Hello world")]
    [InlineData("erm hello world", "Hello world")]
    [InlineData("umm hello world", "Hello world")]
    public void Clean_Light_RemovesStandaloneFillers(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("er ist hier", "Er ist hier")]
    [InlineData("um momento", "Um momento")]
    [InlineData("er zijn drie boeken", "Er zijn drie boeken")]
    [InlineData("er bil", "Er bil")]
    [InlineData("jeg er glad", "Jeg er glad")]
    public void Clean_Light_PreservesGermanAndPortugueseWordsThatCollideWithOldFillerPatterns(
        string input,
        string expected
    )
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_DoesNotDamageWordsContainingFillerText()
    {
        var result = _sut.Clean("umbrella error ahead", CleanupLevel.Light);

        Assert.Equal("Umbrella error ahead", result);
    }

    [Fact]
    public void Clean_Light_NormalizesWhitespaceAndPreservesSentencePunctuation()
    {
        var result = _sut.Clean("  hello   world  .  ", CleanupLevel.Light);

        Assert.Equal("Hello world.", result);
    }

    [Theory]
    [InlineData("send it to John actually Jane", "Send it to Jane")]
    [InlineData("set the color to red I mean blue", "Set the color to blue")]
    public void Clean_Light_AppliesConservativeOneWordBacktrackCorrections(
        string input,
        string expected
    )
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_AppliesScratchThatReplacementWhenReplacementLooksLikeNewDictation()
    {
        var result = _sut.Clean(
            "send the first draft scratch that please send the final draft",
            CleanupLevel.Light
        );

        Assert.Equal("Please send the final draft", result);
    }

    [Theory]
    [InlineData("please scratch that surface carefully", "Please scratch that surface carefully")]
    [InlineData(
        "we meet Tuesday actually Wednesday then review notes",
        "We meet Tuesday actually Wednesday then review notes"
    )]
    public void Clean_Light_LeavesAmbiguousBacktrackTextAlone(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_FormatsClearSpokenNumberedList()
    {
        var result = _sut.Clean("one apples two bananas three oranges", CleanupLevel.Light);

        Assert.Equal("1. Apples\n2. bananas\n3. oranges", result);
    }

    [Theory]
    [InlineData("bullet list apples bananas oranges", "- Apples\n- bananas\n- oranges")]
    [InlineData("bullet list apples comma bananas comma oranges", "- Apples\n- bananas\n- oranges")]
    [InlineData(
        "bullet list apples next bullet bananas next bullet oranges",
        "- Apples\n- bananas\n- oranges"
    )]
    public void Clean_Light_FormatsClearSpokenBulletList(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_DoesNotFormatAmbiguousBulletList()
    {
        var result = _sut.Clean("bullet list the things we need", CleanupLevel.Light);

        Assert.Equal("Bullet list the things we need", result);
    }

    [Theory]
    [InlineData("hello comma world period", "Hello, world.")]
    [InlineData("are you ready question mark", "Are you ready?")]
    [InlineData(
        "warning colon check disk semicolon restart later",
        "Warning: check disk; restart later"
    )]
    [InlineData("great work exclamation point", "Great work!")]
    public void Clean_Light_AppliesSpokenPunctuation(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("first period", "First period")]
    [InlineData("third comma", "Third comma")]
    [InlineData("version one period two", "Version one period two")]
    [InlineData("first period second period", "First period second.")]
    public void Clean_Light_LeavesAmbiguousSpokenPunctuationAsWords(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_DoesNotFormatOutOfOrderNumberedList()
    {
        var result = _sut.Clean("one apples three oranges", CleanupLevel.Light);

        Assert.Equal("One apples three oranges", result);
    }

    [Fact]
    public void Clean_MediumAndHigh_DegradeToLightForNow()
    {
        Assert.Equal("Hello", _sut.Clean("uh hello", CleanupLevel.Medium));
        Assert.Equal("Hello", _sut.Clean("uh hello", CleanupLevel.High));
    }

    [Fact]
    public void GetLlmSystemPrompt_ReturnsMediumPrompt()
    {
        var result = CleanupService.GetLlmSystemPrompt(CleanupLevel.Medium);

        Assert.Contains("Improve readability", result);
        Assert.Contains("Do not add new information", result);
    }

    [Fact]
    public void GetLlmSystemPrompt_ReturnsHighPrompt()
    {
        var result = CleanupService.GetLlmSystemPrompt(CleanupLevel.High);

        Assert.Contains("Rewrite as concise polished prose", result);
        Assert.Contains("Do not add new information", result);
    }

    [Fact]
    public void Clean_Light_AppliesRepairs04_ScratchThatImperativeReplacement()
    {
        var result = _sut.Clean(
            "send the report tomorrow scratch that save it as a draft",
            CleanupLevel.Light
        );

        Assert.Equal("Save it as a draft", result);
    }

    [Theory]
    [InlineData("Send the report, no wait, save it as a draft", "Save it as a draft")]
    [InlineData("send the report tomorrow no, wait, save it as a draft", "Save it as a draft")]
    [InlineData("send the report tomorrow never mind save it as a draft", "Save it as a draft")]
    [InlineData("send the report tomorrow cancel that save it as a draft", "Save it as a draft")]
    public void Clean_Light_AppliesRepairMarkers_ReplacesRetractedClause(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_AppliesRepairs02_OneWordCorrectionWithRepeatedNoun()
    {
        var result = _sut.Clean(
            "send 47 files I mean 74 files",
            CleanupLevel.Light
        );

        Assert.Equal("Send 74 files", result);
    }

    [Fact]
    public void Clean_Light_AppliesProfiles01_FormalEmailGreetingAndSignoff()
    {
        var result = _sut.Clean(
            "hi Rina um can you review the draft today thanks",
            CleanupLevel.Light,
            "en",
            ProfileStylePreset.FormalEmail
        );

        Assert.Equal("Hi Rina,\n\nCan you review the draft today?\n\nThanks.", result);
    }

    [Fact]
    public void Clean_Light_AppliesProfiles03_OrdinalNumberedListWithIntroClause()
    {
        var result = _sut.Clean(
            "we need three things first tests second logs third benchmarks",
            CleanupLevel.Light
        );

        Assert.Equal("We need three things:\n1. Tests\n2. Logs\n3. Benchmarks.", result);
    }

    [Fact]
    public void GetLlmSystemPrompt_ContainsExplicitRepairMarkers()
    {
        var medium = CleanupService.GetLlmSystemPrompt(CleanupLevel.Medium);
        var high = CleanupService.GetLlmSystemPrompt(CleanupLevel.High);

        Assert.Contains("Resolve explicit self-corrections and repair markers (such as scratch that, no wait, never mind, cancel that, I mean, actually)", medium);
        Assert.Contains("Resolve explicit self-corrections and repair markers (such as scratch that, no wait, never mind, cancel that, I mean, actually)", high);
    }

    [Theory]
    [InlineData(ProfileStylePreset.FormalEmail, "Format as a readable email with finished sentences")]
    [InlineData(ProfileStylePreset.CasualMessage, "Keep a natural, casual chat tone with clean sentence punctuation.")]
    [InlineData(ProfileStylePreset.MeetingNotes, "When the speaker dictates an explicit list (such as 'first... second... third...'), format it as a clean numbered or bulleted list without adding extra items.")]
    public void GetLlmSystemPrompt_WithStylePresets_AppendsPresetInstructions(ProfileStylePreset preset, string expectedSuffixFragment)
    {
        var prompt = CleanupService.GetLlmSystemPrompt(CleanupLevel.Medium, preset);

        Assert.Contains(expectedSuffixFragment, prompt);
    }
}
