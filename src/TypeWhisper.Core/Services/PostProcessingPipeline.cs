using System.Diagnostics;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Priority-based post-processing pipeline. Steps are sorted by priority (ascending)
///     and executed sequentially. Built-in priorities:
///     Plugin PostProcessors: their own Priority value
///     Cleanup: 250
///     LLM Prompt Action: 300
///     Snippet Expansion: 500
///     Vocabulary Boosting: 550
///     Dictionary Corrections: 600
///     Translation: 900 (always last)
/// </summary>
public sealed class PostProcessingPipeline : IPostProcessingPipeline
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
        if (options.RequireLlmSuccess && options.LlmHandler is null)
        {
            throw new InvalidOperationException(
                "Required LLM post-processing is not configured."
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
                // Continue with current text — don't let one step break the pipeline
            }
        }

        return new PostProcessingResult { Text = text, Steps = stepResults };
    }

    private static List<(
        int Priority,
        string Name,
        Func<string, CancellationToken, Task<string>> Execute
        )> BuildSteps(PipelineOptions options)
    {
        var steps = new List<(int, string, Func<string, CancellationToken, Task<string>>)>();

        // Spoken line-break commands run first so the LLM (and everything
        // after) sees real line breaks instead of the words "new line".
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

        // Spoken punctuation names ("question mark", "exclamation point") run
        // right after line breaks so the symbol — not the words — reaches the
        // LLM and everything downstream.
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

        // Plugin post-processors insert at their own Priority; context is captured in each closure
        if (options.PluginPostProcessors is { Count: > 0 } processors)
        {
            foreach (var processor in processors)
            {
                var p = processor;
                steps.Add(
                    (
                        p.Priority,
                        $"{PostProcessingStepNames.PluginPrefix}{p.Priority})",
                        p.ProcessAsync
                    )
                );
            }
        }

        // Cleanup runs before LLM/snippets so the AI prompt receives already-cleaned text
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

        // Translation is always last (priority 900) so it operates on the fully post-processed text
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

    // STT renders the spoken command in varied ways ("new line", "New Line.",
    // "newline"), often as its own little sentence with stray punctuation
    // around it ("Club? New Line. Should"). These patterns absorb the command
    // plus the trailing comma/period and surrounding spaces the recognizer pads
    // it with, while leaving any preceding sentence punctuation intact
    // ("Club?\nShould"). Order matters: "new paragraph" before "new line" so the
    // longer phrase wins.
    private static readonly Regex s_newParagraph = new(
        @"[ \t]*\bnew\s+paragraph\b[ \t]*[.,]?[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex s_newLine = new(
        @"[ \t]*\b(?:new\s+line|newline)\b[ \t]*[.,]?[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    ///     Converts the spoken commands "new paragraph" and "new line"/"newline"
    ///     into literal line breaks. Deterministic on purpose — LLMs do not
    ///     reliably honor these verbal commands. Caveat: this also fires when
    ///     "new line" is meant literally (e.g. "a new line of code"); that's the
    ///     accepted trade-off dictation tools make for the command.
    /// </summary>
    internal static string NormalizeSpokenLineBreaks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = s_newParagraph.Replace(text, "\n\n");
        text = s_newLine.Replace(text, "\n");
        return text;
    }

    // Only the high-confidence spoken-punctuation phrases are handled here.
    // "question mark" and "exclamation point/mark" are almost never dictated as
    // literal content, so converting them deterministically is safe. We
    // deliberately do NOT touch "period"/"comma"/"colon"/"dash" — those are
    // common content words ("during that period", "the Oxford comma"), so the
    // collision risk outweighs the benefit; the fine-tuned LLM handles those.
    // The match absorbs the horizontal whitespace AROUND the phrase (and any
    // stray sentence punctuation the recognizer pads it with), then re-emits a
    // single trailing space so a sentence-internal "X question mark Y" becomes
    // "X? Y". Crucially the whitespace handling is LOCAL to the match — we never
    // collapse whitespace elsewhere in the transcript, so indentation, aligned
    // columns, and code snippets are left intact.
    private static readonly Regex s_questionMark = new(
        @"[ \t]*\bquestion\s+mark\b[ \t]*[.!?]*[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex s_exclamation = new(
        @"[ \t]*\bexclamation\s+(?:point|mark)\b[ \t]*[.!?]*[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // Drops the single trailing space we insert after a converted ?/! when it
    // lands at end-of-text or end-of-line. Matches ONLY a "?"/"!" followed by
    // exactly one space then a newline/end, so it can't touch indentation or
    // other intentional whitespace.
    private static readonly Regex s_trailingInsertedSpace = new(
        @"([?!]) (?=\n|$)",
        RegexOptions.Compiled
    );

    /// <summary>
    ///     Converts the unambiguous spoken punctuation phrases "question mark"
    ///     and "exclamation point"/"exclamation mark" into the literal
    ///     <c>?</c>/<c>!</c> symbols. Deterministic on purpose — the model is
    ///     intermittent on these, and the STT engine itself only sometimes
    ///     converts them, so a code pass is the reliable safety net. Caveat:
    ///     this also fires when the phrase is meant literally ("add a question
    ///     mark there" → "add a? there"); that's the accepted trade-off, the
    ///     same one <see cref="NormalizeSpokenLineBreaks" /> makes for "new
    ///     line". Whitespace cleanup is confined to the replacement site, so it
    ///     does not rewrite formatting elsewhere in the dictation.
    /// </summary>
    internal static string NormalizeSpokenPunctuation(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = s_questionMark.Replace(text, "? ");
        text = s_exclamation.Replace(text, "! ");
        text = s_trailingInsertedSpace.Replace(text, "$1");
        return text;
    }
}