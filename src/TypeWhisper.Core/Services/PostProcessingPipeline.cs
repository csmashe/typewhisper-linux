using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Priority-based post-processing pipeline. Steps are sorted by priority (ascending)
/// and executed sequentially. Built-in priorities:
///   Plugin PostProcessors: their own Priority value
///   Cleanup: 250
///   LLM Prompt Action: 300
///   Snippet Expansion: 500
///   Vocabulary Boosting: 550
///   Dictionary Corrections: 600
///   Translation: 900 (always last)
/// </summary>
public sealed class PostProcessingPipeline : IPostProcessingPipeline
{
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
        CancellationToken ct = default)
    {
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
                stepResults.Add(new PostProcessingStepResult(
                    name,
                    !string.Equals(before, text, StringComparison.Ordinal)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"PostProcessingPipeline: Step '{name}' failed: {ex.Message}");
                stepResults.Add(new PostProcessingStepResult(
                    name,
                    Changed: false,
                    Succeeded: false,
                    ErrorMessage: ex.Message));
                // Continue with current text — don't let one step break the pipeline
            }
        }

        return new PostProcessingResult
        {
            Text = text,
            Steps = stepResults
        };
    }

    private static List<(int Priority, string Name, Func<string, CancellationToken, Task<string>> Execute)>
        BuildSteps(PipelineOptions options)
    {
        var steps = new List<(int, string, Func<string, CancellationToken, Task<string>>)>();

        if (options.AppFormatter is not null)
        {
            var processName = options.TargetProcessName;
            steps.Add((FormattingPriority, PostProcessingStepNames.Formatting,
                (text, _) => Task.FromResult(options.AppFormatter(text, processName))));
        }

        // Plugin post-processors insert at their own Priority; context is captured in each closure
        if (options.PluginPostProcessors is { Count: > 0 } processors)
        {
            foreach (var processor in processors)
            {
                var p = processor;
                steps.Add((p.Priority, $"Plugin({p.Priority})", p.ProcessAsync));
            }
        }

        // Cleanup runs before LLM/snippets so the AI prompt receives already-cleaned text
        if (options.CleanupHandler is not null)
        {
            steps.Add((CleanupPriority, PostProcessingStepNames.Cleanup, options.CleanupHandler));
        }

        if (options.LlmHandler is not null)
        {
            steps.Add((LlmPriority, PostProcessingStepNames.Llm,
                async (text, ct) =>
                {
                    if (options.StatusCallback is not null)
                        await options.StatusCallback("AI");
                    return await options.LlmHandler(text, ct);
                }));
        }

        if (options.SnippetExpander is not null)
        {
            steps.Add((SnippetPriority, PostProcessingStepNames.Snippets,
                (text, _) => Task.FromResult(options.SnippetExpander(text))));
        }

        if (options.VocabularyBooster is not null)
        {
            steps.Add((VocabularyBoostingPriority, PostProcessingStepNames.VocabularyBoosting,
                (text, _) => Task.FromResult(options.VocabularyBooster(text))));
        }

        if (options.DictionaryCorrector is not null)
        {
            steps.Add((DictionaryPriority, PostProcessingStepNames.Dictionary,
                (text, _) => Task.FromResult(options.DictionaryCorrector(text))));
        }

        // Translation is always last (priority 900) so it operates on the fully post-processed text
        if (options.TranslationHandler is not null && !string.IsNullOrEmpty(options.TranslationTarget))
        {
            var detectedLang = options.DetectedLanguage;
            var effectiveLang = options.EffectiveSourceLanguage;
            var targetLang = options.TranslationTarget;

            steps.Add((TranslationPriority, PostProcessingStepNames.Translation,
                async (text, ct) =>
                {
                    var sourceLang = detectedLang ?? effectiveLang ?? "auto";
                    if (sourceLang == targetLang)
                        return text;

                    if (options.StatusCallback is not null)
                        await options.StatusCallback("Translation");
                    return await options.TranslationHandler(text, sourceLang, targetLang, ct);
                }));
        }

        steps.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return steps;
    }
}
