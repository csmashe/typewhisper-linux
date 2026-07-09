using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="PostProcessingPipeline" />: step ordering, spoken normalization, translation gating, cancellation, and error resilience.</summary>
public class PostProcessingPipelineTests
{
    private readonly PostProcessingPipeline _sut = new();

    [Fact]
    public async Task ProcessAsync_NoOptions_ReturnsRawText()
    {
        var result = await _sut.ProcessAsync("hello world", new PipelineOptions());
        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_SpokenLineBreaks_Disabled_LeavesCommandWords()
    {
        var result = await _sut.ProcessAsync(
            "When is reading club new line should be tomorrow.",
            new PipelineOptions { NormalizeSpokenLineBreaks = false }
        );
        Assert.Equal("When is reading club new line should be tomorrow.", result.Text);
    }

    [Theory]
    // "new line" becomes a single break; the trailing period/space is absorbed.
    [InlineData("When is reading club new line should be tomorrow.",
        "When is reading club\nshould be tomorrow.")]
    // A preceding ? is preserved.
    [InlineData("When is Reading Club? New Line. Should be tomorrow.",
        "When is Reading Club?\nShould be tomorrow.")]
    // "new paragraph" -> blank line; the preceding sentence period stays.
    [InlineData("First point. New paragraph. Second point.",
        "First point.\n\nSecond point.")]
    // single-token "newline" spelling works too.
    [InlineData("line one newline line two", "line one\nline two")]
    public async Task ProcessAsync_SpokenLineBreaks_Enabled_Converts(string input, string expected)
    {
        var result = await _sut.ProcessAsync(
            input,
            new PipelineOptions { NormalizeSpokenLineBreaks = true }
        );
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public async Task ProcessAsync_SpokenLineBreaks_NewParagraphTakesPrecedenceOverNewLine()
    {
        // The longer phrase must win so "new paragraph" isn't half-eaten by the
        // "new line" pattern.
        var result = await _sut.ProcessAsync(
            "a new paragraph b",
            new PipelineOptions { NormalizeSpokenLineBreaks = true }
        );
        Assert.Equal("a\n\nb", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_SpokenPunctuation_Disabled_LeavesPhrase()
    {
        var result = await _sut.ProcessAsync(
            "can you review this question mark",
            new PipelineOptions { NormalizeSpokenPunctuation = false }
        );
        Assert.Equal("can you review this question mark", result.Text);
    }

    [Theory]
    // End-of-utterance "question mark" -> "?", trailing space trimmed.
    [InlineData("can you review this question mark", "can you review this?")]
    // STT often pads the phrase with its own period; it's absorbed.
    [InlineData("can you review this question mark.", "can you review this?")]
    // Sentence-internal: a single following space is preserved.
    [InlineData("is this right question mark and then we ship",
        "is this right? and then we ship")]
    // "exclamation point" and "exclamation mark" both -> "!".
    [InlineData("we won the deal exclamation point", "we won the deal!")]
    [InlineData("we won the deal exclamation mark", "we won the deal!")]
    // STT already emitted the symbol AND left the words: dedupe to one symbol.
    [InlineData("can you review this question mark?", "can you review this?")]
    public async Task ProcessAsync_SpokenPunctuation_Enabled_Converts(string input, string expected)
    {
        var result = await _sut.ProcessAsync(
            input,
            new PipelineOptions { NormalizeSpokenPunctuation = true }
        );
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    // Common content words are deliberately NOT converted (collision risk);
    // the model handles these.
    [InlineData("the price went up during that period")]
    [InlineData("we forgot the oxford comma again")]
    public async Task ProcessAsync_SpokenPunctuation_LeavesContentWords(string input)
    {
        var result = await _sut.ProcessAsync(
            input,
            new PipelineOptions { NormalizeSpokenPunctuation = true }
        );
        Assert.Equal(input, result.Text);
    }

    [Fact]
    public async Task ProcessAsync_SpokenPunctuation_DoesNotEatTrailingSentence()
    {
        // The capital is left for the LLM; this pass only fixes the symbol and
        // spacing, it does not collapse the following sentence into the prior.
        var result = await _sut.ProcessAsync(
            "are we done question mark. Let me know",
            new PipelineOptions { NormalizeSpokenPunctuation = true }
        );
        Assert.Equal("are we done? Let me know", result.Text);
    }

    [Theory]
    // Whitespace cleanup must be LOCAL to the converted phrase — indentation,
    // tabs, and aligned columns elsewhere in the transcript must survive even
    // when a spoken-punctuation phrase is present.
    [InlineData("we won the deal exclamation point\n\tstill   aligned",
        "we won the deal!\n\tstill   aligned")]
    [InlineData("is it ready question mark\ncol1   col2\tcol3",
        "is it ready?\ncol1   col2\tcol3")]
    // Multiple spaces with no spoken-punctuation phrase are left untouched.
    [InlineData("plain   text\twith\tgaps", "plain   text\twith\tgaps")]
    public async Task ProcessAsync_SpokenPunctuation_PreservesUnrelatedWhitespace(
        string input,
        string expected
    )
    {
        var result = await _sut.ProcessAsync(
            input,
            new PipelineOptions { NormalizeSpokenPunctuation = true }
        );
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public async Task ProcessAsync_DictionaryCorrections_Applied()
    {
        var options = new PipelineOptions
        {
            DictionaryCorrector = text => text.Replace("teh", "the")
        };

        var result = await _sut.ProcessAsync("teh quick fox", options);
        Assert.Equal("the quick fox", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_SnippetExpansion_Applied()
    {
        var options = new PipelineOptions
        {
            SnippetExpander = text => text.Replace("brb", "be right back")
        };

        var result = await _sut.ProcessAsync("brb", options);
        Assert.Equal("be right back", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsStepChangeMetadata()
    {
        var options = new PipelineOptions
        {
            CleanupHandler = (text, _) => Task.FromResult(text.Trim()),
            SnippetExpander = text => text.Replace("brb", "be right back"),
            DictionaryCorrector = text => text
        };

        var result = await _sut.ProcessAsync(" brb ", options);

        Assert.Equal("be right back", result.Text);
        Assert.Contains(result.Steps, step => step is { Name: "Cleanup", Changed: true });
        Assert.Contains(result.Steps, step => step is { Name: "Snippets", Changed: true });
        Assert.Contains(result.Steps, step => step is { Name: "Dictionary", Changed: false });
    }

    [Fact]
    public async Task ProcessAsync_LlmHandler_Applied()
    {
        var options = new PipelineOptions
        {
            LlmHandler = (text, _) => Task.FromResult(text.ToUpperInvariant())
        };

        var result = await _sut.ProcessAsync("hello", options);
        Assert.Equal("HELLO", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_RequiredLlmHandlerFailure_Throws()
    {
        var options = new PipelineOptions
        {
            LlmHandler = (_, _) => throw new InvalidOperationException("LLM failed"),
            RequireLlmSuccess = true
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ProcessAsync("raw transcript", options)
        );
        Assert.Equal("LLM failed", ex.Message);
    }

    [Fact]
    public async Task ProcessAsync_RequiredLlmWithoutHandler_Throws()
    {
        var options = new PipelineOptions { RequireLlmSuccess = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ProcessAsync("raw transcript", options)
        );
    }

    [Fact]
    public async Task ProcessAsync_Translation_Applied()
    {
        var options = new PipelineOptions
        {
            TranslationHandler = (text, _, tgt, _) => Task.FromResult($"[{tgt}] {text}"),
            TranslationTarget = "fr",
            DetectedLanguage = "en"
        };

        var result = await _sut.ProcessAsync("hello", options);
        Assert.Equal("[fr] hello", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_Translation_UsesDetectedLanguageWhenEffectiveLanguageMatchesTarget()
    {
        string? sourceLanguage = null;
        var options = new PipelineOptions
        {
            TranslationHandler = (text, src, tgt, _) =>
            {
                sourceLanguage = src;
                return Task.FromResult($"[{tgt}] {text}");
            },
            TranslationTarget = "it",
            EffectiveSourceLanguage = "it",
            DetectedLanguage = "en"
        };

        var result = await _sut.ProcessAsync("ciao mondo", options);

        Assert.Equal("[it] ciao mondo", result.Text);
        Assert.Equal("en", sourceLanguage);
    }

    [Fact]
    public async Task ProcessAsync_Translation_SkippedWhenSameLanguage()
    {
        var options = new PipelineOptions
        {
            TranslationHandler = (text, _, tgt, _) => Task.FromResult($"[{tgt}] {text}"),
            TranslationTarget = "en",
            DetectedLanguage = "en"
        };

        var result = await _sut.ProcessAsync("hello", options);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_PriorityOrdering_PluginsBeforeLlm()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(
                    100,
                    (text, _) =>
                    {
                        executionOrder.Add("Plugin100");
                        return Task.FromResult(text + "+P100");
                    }
                )
            ],
            LlmHandler = (text, _) =>
            {
                executionOrder.Add("LLM");
                return Task.FromResult(text + "+LLM");
            },
            SnippetExpander = text =>
            {
                executionOrder.Add("Snippets");
                return text + "+SNP";
            },
            VocabularyBooster = text =>
            {
                executionOrder.Add("Boosting");
                return text + "+BOOST";
            },
            DictionaryCorrector = text =>
            {
                executionOrder.Add("Dictionary");
                return text + "+DICT";
            }
        };

        var result = await _sut.ProcessAsync("start", options);

        // Priority order: Plugin(100) → LLM(300) → Snippets(500) → Boosting(550) → Dictionary(600)
        Assert.Equal(["Plugin100", "LLM", "Snippets", "Boosting", "Dictionary"], executionOrder);
        Assert.Equal("start+P100+LLM+SNP+BOOST+DICT", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_NumberNormalization_RunsBeforeLaterPostProcessing()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = true,
            TranscriptionTask = TranscriptionTask.Transcribe,
            DetectedLanguage = "en",
            ConfiguredLanguage = "en",
            AppFormatter = (text, _) =>
            {
                executionOrder.Add($"Formatting:{text}");
                return text + "+FMT";
            },
            LlmHandler = (text, _) =>
            {
                executionOrder.Add($"LLM:{text}");
                return Task.FromResult(text + "+LLM");
            },
            SnippetExpander = text =>
            {
                executionOrder.Add($"Snippets:{text}");
                return text + "+SNP";
            },
            VocabularyBooster = text =>
            {
                executionOrder.Add($"Boosting:{text}");
                return text + "+BOOST";
            },
            DictionaryCorrector = text =>
            {
                executionOrder.Add($"Dictionary:{text}");
                return text + "+DICT";
            },
            TranslationHandler = (text, _, _, _) =>
            {
                executionOrder.Add($"Translation:{text}");
                return Task.FromResult(text + "+TR");
            },
            TranslationTarget = "fr"
        };

        var result = await _sut.ProcessAsync("twenty three", options);

        Assert.Equal("23+FMT+LLM+SNP+BOOST+DICT+TR", result.Text);
        Assert.Equal(
            [
                "Formatting:23",
                "LLM:23+FMT",
                "Snippets:23+FMT+LLM",
                "Boosting:23+FMT+LLM+SNP",
                "Dictionary:23+FMT+LLM+SNP+BOOST",
                "Translation:23+FMT+LLM+SNP+BOOST+DICT"
            ],
            executionOrder);
    }

    [Fact]
    public async Task ProcessAsync_NumberNormalizationGloballyDisabled_PreservesWords()
    {
        var options = new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = false,
            DetectedLanguage = "en"
        };

        var result = await _sut.ProcessAsync("twenty three", options);

        Assert.Equal("twenty three", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_NumberNormalizationGloballyEnabled_NormalizesWords()
    {
        var options = new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = true,
            DetectedLanguage = "en"
        };

        var result = await _sut.ProcessAsync("twenty three", options);

        Assert.Equal("23", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_NumberNormalization_UsesLaterConfiguredLanguageCandidate()
    {
        var options = new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = true,
            DetectedLanguage = "de",
            ConfiguredLanguage = "de",
            ConfiguredLanguageCandidates = ["de", "en"]
        };

        var result = await _sut.ProcessAsync("Set the value to twenty three", options);

        Assert.Equal("Set the value to 23", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_NumberNormalization_UsesEnglishForTranslateTask()
    {
        var options = new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = true,
            TranscriptionTask = TranscriptionTask.Translate,
            DetectedLanguage = "de",
            ConfiguredLanguage = "de"
        };

        var result = await _sut.ProcessAsync("twenty three", options);

        Assert.Equal("23", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_Cleanup_RunsBeforeLlmAndSnippets()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(
                    100,
                    (text, _) =>
                    {
                        executionOrder.Add("Plugin100");
                        return Task.FromResult(text + "+P100");
                    }
                )
            ],
            CleanupHandler = (text, _) =>
            {
                executionOrder.Add("Cleanup");
                return Task.FromResult(text + "+CLEAN");
            },
            LlmHandler = (text, _) =>
            {
                executionOrder.Add("LLM");
                return Task.FromResult(text + "+LLM");
            },
            SnippetExpander = text =>
            {
                executionOrder.Add("Snippets");
                return text + "+SNP";
            }
        };

        var result = await _sut.ProcessAsync("start", options);

        Assert.Equal(["Plugin100", "Cleanup", "LLM", "Snippets"], executionOrder);
        Assert.Equal("start+P100+CLEAN+LLM+SNP", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_MultiplePlugins_SortedByPriority()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(
                    700,
                    (text, _) =>
                    {
                        executionOrder.Add("Plugin700");
                        return Task.FromResult(text + "+P700");
                    }
                ),
                new PluginPostProcessor(
                    50,
                    (text, _) =>
                    {
                        executionOrder.Add("Plugin50");
                        return Task.FromResult(text + "+P50");
                    }
                ),
                new PluginPostProcessor(
                    400,
                    (text, _) =>
                    {
                        executionOrder.Add("Plugin400");
                        return Task.FromResult(text + "+P400");
                    }
                )
            ]
        };

        var result = await _sut.ProcessAsync("start", options);

        // Plugin50(50) → Plugin400(400) → Plugin700(700)
        Assert.Equal(["Plugin50", "Plugin400", "Plugin700"], executionOrder);
        Assert.Equal("start+P50+P400+P700", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_PluginBetweenLlmAndSnippets()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(
                    400,
                    (text, _) =>
                    {
                        executionOrder.Add("Plugin400");
                        return Task.FromResult(text);
                    }
                )
            ],
            LlmHandler = (text, _) =>
            {
                executionOrder.Add("LLM");
                return Task.FromResult(text);
            },
            SnippetExpander = text =>
            {
                executionOrder.Add("Snippets");
                return text;
            }
        };

        await _sut.ProcessAsync("test", options);

        // LLM(300) → Plugin(400) → Snippets(500)
        Assert.Equal(["LLM", "Plugin400", "Snippets"], executionOrder);
    }

    [Fact]
    public async Task ProcessAsync_ErrorResilience_ContinuesAfterFailure()
    {
        var options = new PipelineOptions
        {
            PluginPostProcessors =
            [
                new PluginPostProcessor(
                    100,
                    (_, _) => throw new InvalidOperationException("Plugin failed")
                )
            ],
            DictionaryCorrector = text => text + "+DICT"
        };

        var result = await _sut.ProcessAsync("hello", options);

        // Plugin failed but dictionary still applied
        Assert.Equal("hello+DICT", result.Text);
        Assert.Contains(
            result.Steps,
            step => step is { Succeeded: false, ErrorMessage: "Plugin failed" }
        );
    }

    [Fact]
    public async Task ProcessAsync_Translation_UsesAutoWhenSourceUnknown()
    {
        string? sourceLanguage = null;
        var options = new PipelineOptions
        {
            TranslationHandler = (text, src, _, _) =>
            {
                sourceLanguage = src;
                return Task.FromResult(text);
            },
            TranslationTarget = "fr"
        };

        await _sut.ProcessAsync("bonjour", options);

        Assert.Equal("auto", sourceLanguage);
    }

    [Fact]
    public async Task ProcessAsync_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var options = new PipelineOptions { DictionaryCorrector = text => text };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _sut.ProcessAsync("test", options, cts.Token)
        );
    }

    [Fact]
    public async Task ProcessAsync_StatusCallback_CalledForLlmAndTranslation()
    {
        var statusCalls = new List<string>();

        var options = new PipelineOptions
        {
            LlmHandler = (text, _) => Task.FromResult(text),
            TranslationHandler = (text, _, _, _) => Task.FromResult(text),
            TranslationTarget = "fr",
            DetectedLanguage = "en",
            StatusCallback = status =>
            {
                statusCalls.Add(status);
                return Task.CompletedTask;
            }
        };

        await _sut.ProcessAsync("test", options);

        Assert.Contains("AI", statusCalls);
        Assert.Contains("Translation", statusCalls);
    }

    [Fact]
    public async Task ProcessAsync_TranslationAlwaysLast()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            DictionaryCorrector = text =>
            {
                executionOrder.Add("Dictionary");
                return text;
            },
            TranslationHandler = (text, _, _, _) =>
            {
                executionOrder.Add("Translation");
                return Task.FromResult(text);
            },
            TranslationTarget = "fr",
            DetectedLanguage = "en"
        };

        await _sut.ProcessAsync("test", options);

        Assert.Equal("Dictionary", executionOrder[0]);
        Assert.Equal("Translation", executionOrder[1]);
    }

    [Fact]
    public async Task ProcessAsync_VocabularyBoosting_RunsBeforeDictionary()
    {
        var executionOrder = new List<string>();

        var options = new PipelineOptions
        {
            VocabularyBooster = text =>
            {
                executionOrder.Add("Boosting");
                return text.Replace("type whisper", "TypeWhisper");
            },
            DictionaryCorrector = text =>
            {
                executionOrder.Add("Dictionary");
                return text.Replace("TypeWhisper", "TYPEWHISPER");
            }
        };

        var result = await _sut.ProcessAsync("type whisper", options);

        Assert.Equal(["Boosting", "Dictionary"], executionOrder);
        Assert.Equal("TYPEWHISPER", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_VocabularyBoostingDisabled_LeavesTextUnchanged()
    {
        var options = new PipelineOptions { DictionaryCorrector = text => text };

        var result = await _sut.ProcessAsync("type whisper", options);

        Assert.Equal("type whisper", result.Text);
    }

    [Fact]
    public async Task ProcessAsync_OutlookFormatting_DoesNotEmitHtmlTags()
    {
        var options = new PipelineOptions
        {
            AppFormatter = AppFormatterService.Format,
            TargetProcessName = "OUTLOOK"
        };

        var result = await _sut.ProcessAsync("- one\n- two", options);

        Assert.Equal("- one\n- two", result.Text);
        Assert.DoesNotContain("<", result.Text);
        Assert.DoesNotContain(">", result.Text);
    }
}