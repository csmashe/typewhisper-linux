using System.Diagnostics;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Priority-based post-processing pipeline. Steps run in ascending priority order:
///     SpokenCommands=50, SpokenPunctuation=60, Formatting=150, Cleanup=250,
///     LLM=300, Snippets=500, VocabularyBoosting=550, Dictionary=600, Translation=900.
///     Plugin post-processors insert at their own declared priority.
/// </summary>
public sealed partial class PostProcessingPipeline : IPostProcessingPipeline
{
    private const int SpokenCommandsPriority = 50;
    private const int SpokenPunctuationPriority = 60;
    private const int FormattingPriority = 150;
    private const int CleanupPriority = 250;
    private const int LlmPriority = 300;
    private const int SnippetPriority = 500;
    private const int VocabularyBoostingPriority = 550;
    private const int DictionaryPriority = 600;
    private const int TranslationPriority = 900;

    public async Task<PostProcessingResult> ProcessAsync(
        string rawText,
        PipelineOptions options,
        CancellationToken ct = default
    )
    {
        if (options is { RequireLlmSuccess: true, LlmHandler: null })
        {
            throw new InvalidOperationException(
                "Required LLM post-processing is not configured."
            );
        }

        if (options.RequireTranslationSuccess
            && (options.TranslationHandler is null
                || string.IsNullOrWhiteSpace(options.TranslationTarget)))
        {
            throw new InvalidOperationException(
                "Required translation is not configured."
            );
        }

        var steps = BuildSteps(options);
        var text = rawText;
        var stepResults = new List<PostProcessingStepResult>();

        foreach (var (_, name, executor) in steps)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var before = text;
                text = await executor(text, ct);
                stepResults.Add(
                    new PostProcessingStepResult(
                        name,
                        !string.Equals(before, text, StringComparison.Ordinal)
                    )
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"PostProcessingPipeline: Step '{name}' failed: {ex.Message}"
                );
                stepResults.Add(
                    new PostProcessingStepResult(
                        name,
                        false,
                        false,
                        ex.Message
                    )
                );
                if (name == PostProcessingStepNames.Llm && options.RequireLlmSuccess)
                {
                    throw;
                }

                if (name == PostProcessingStepNames.Translation && options.RequireTranslationSuccess)
                {
                    throw;
                }
                // Continue on current text so one failing step doesn't break the whole pipeline.
            }
        }

        return new PostProcessingResult { Text = text, Steps = stepResults };
    }

    /// <summary>
    ///     Converts "new paragraph"/"new line"/"newline" into literal line breaks.
    ///     Deterministic because LLMs don't reliably honor these verbal commands.
    ///     Caveat: also fires when the phrase is literal content (e.g. "a new line of code").
    /// </summary>
    private static string NormalizeSpokenLineBreaks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = NewParagraphRegex().Replace(text, "\n\n");
        text = NewLineRegex().Replace(text, "\n");
        return text;
    }

    /// <summary>
    ///     Converts "question mark" and "exclamation point/mark" to <c>?</c>/<c>!</c>.
    ///     Deterministic because the fine-tuned model and STT engine are both intermittent on these.
    ///     Caveat: also fires when the phrase is literal content (same trade-off as
    ///     <see cref="NormalizeSpokenLineBreaks" />). Whitespace cleanup is local to each replacement.
    /// </summary>
    private static string NormalizeSpokenPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = QuestionMarkRegex().Replace(text, "? ");
        text = ExclamationRegex().Replace(text, "! ");
        text = TrailingInsertedSpaceRegex().Replace(text, "$1");
        return text;
    }

    private static List<(
        int Priority,
        string Name,
        Func<string, CancellationToken, Task<string>> Execute
        )> BuildSteps(PipelineOptions options)
    {
        var steps = new List<(int, string, Func<string, CancellationToken, Task<string>>)>();

        // Spoken line-break commands run first so the LLM sees real breaks, not the words.
        if (options.NormalizeSpokenLineBreaks)
        {
            steps.Add(
                (
                    SpokenCommandsPriority,
                    PostProcessingStepNames.SpokenCommands,
                    (text, _) => Task.FromResult(NormalizeSpokenLineBreaks(text))
                )
            );
        }

        // Spoken punctuation runs right after line breaks so symbols reach the LLM, not words.
        if (options.NormalizeSpokenPunctuation)
        {
            steps.Add(
                (
                    SpokenPunctuationPriority,
                    PostProcessingStepNames.SpokenPunctuation,
                    (text, _) => Task.FromResult(NormalizeSpokenPunctuation(text))
                )
            );
        }

        if (options.AppFormatter is not null)
        {
            var processName = options.TargetProcessName;
            steps.Add(
                (
                    FormattingPriority,
                    PostProcessingStepNames.Formatting,
                    (text, _) => Task.FromResult(options.AppFormatter(text, processName))
                )
            );
        }

        if (options.PluginPostProcessors is { Count: > 0 } processors)
        {
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var processor in processors)
            {
                steps.Add(
                    (
                        processor.Priority,
                        $"{PostProcessingStepNames.PluginPrefix}{processor.Priority})",
                        processor.ProcessAsync
                    )
                );
            }
        }

        if (options.CleanupHandler is not null)
        {
            steps.Add((CleanupPriority, PostProcessingStepNames.Cleanup, options.CleanupHandler));
        }

        if (options.LlmHandler is not null)
        {
            steps.Add(
                (
                    LlmPriority,
                    PostProcessingStepNames.Llm,
                    async (text, ct) =>
                    {
                        if (options.StatusCallback is not null)
                        {
                            await options.StatusCallback("AI");
                        }

                        return await options.LlmHandler(text, ct);
                    }
                )
            );
        }

        if (options.SnippetExpander is not null)
        {
            steps.Add(
                (
                    SnippetPriority,
                    PostProcessingStepNames.Snippets,
                    (text, _) => Task.FromResult(options.SnippetExpander(text))
                )
            );
        }

        if (options.VocabularyBooster is not null)
        {
            steps.Add(
                (
                    VocabularyBoostingPriority,
                    PostProcessingStepNames.VocabularyBoosting,
                    (text, _) => Task.FromResult(options.VocabularyBooster(text))
                )
            );
        }

        if (options.DictionaryCorrector is not null)
        {
            steps.Add(
                (
                    DictionaryPriority,
                    PostProcessingStepNames.Dictionary,
                    (text, _) => Task.FromResult(options.DictionaryCorrector(text))
                )
            );
        }

        if (
            options.TranslationHandler is not null
            && !string.IsNullOrEmpty(options.TranslationTarget)
        )
        {
            var detectedLang = options.DetectedLanguage;
            var effectiveLang = options.EffectiveSourceLanguage;
            var targetLang = options.TranslationTarget;

            steps.Add(
                (
                    TranslationPriority,
                    PostProcessingStepNames.Translation,
                    async (text, ct) =>
                    {
                        var sourceLang = detectedLang ?? effectiveLang ?? "auto";
                        if (string.Equals(sourceLang, targetLang, StringComparison.OrdinalIgnoreCase))
                        {
                            return text;
                        }

                        if (options.StatusCallback is not null)
                        {
                            await options.StatusCallback("Translation");
                        }

                        return await options.TranslationHandler(text, sourceLang, targetLang, ct);
                    }
                )
            );
        }

        steps.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return steps;
    }

    // STT renders spoken commands inconsistently ("new line", "New Line.", "newline") and
    // often pads them with stray punctuation. These patterns absorb the command plus
    // surrounding spaces/punctuation while preserving preceding sentence punctuation
    // ("Club?\nShould"). "new paragraph" must come before "new line" so the longer phrase wins.
    [GeneratedRegex(@"[ \t]*\bnew\s+paragraph\b[ \t]*[.,]?[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex NewParagraphRegex();

    [GeneratedRegex(@"[ \t]*\b(?:new\s+line|newline)\b[ \t]*[.,]?[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex NewLineRegex();

    // Only high-confidence spoken-punctuation phrases: "question mark" and
    // "exclamation point/mark" are almost never literal content, so deterministic
    // conversion is safe. "period"/"comma"/"colon"/"dash" are common content words
    // so we leave those to the fine-tuned LLM. Whitespace handling is LOCAL to the
    // match — indentation and code snippets elsewhere in the transcript are untouched.
    [GeneratedRegex(@"[ \t]*\bquestion\s+mark\b[ \t]*[.!?]*[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionMarkRegex();

    [GeneratedRegex(@"[ \t]*\bexclamation\s+(?:point|mark)\b[ \t]*[.!?]*[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex ExclamationRegex();

    // Removes the trailing space we insert after ?/! when it lands at end-of-text/line.
    // Matches only "?/!" + one space + newline/end, leaving other whitespace intact.
    [GeneratedRegex(@"([?!]) (?=\n|$)")]
    private static partial Regex TrailingInsertedSpaceRegex();
}