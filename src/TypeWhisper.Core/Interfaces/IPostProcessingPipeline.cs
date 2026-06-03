namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Priority-based post-processing pipeline that applies corrections, snippets,
///     plugin processors, LLM prompts, and translation in a defined order.
/// </summary>
public interface IPostProcessingPipeline
{
    /// <summary>
    ///     Processes transcribed text through the full pipeline.
    /// </summary>
    Task<PostProcessingResult> ProcessAsync(
        string rawText,
        PipelineOptions options,
        CancellationToken ct = default
    );
}

/// <summary>
///     Options for configuring the post-processing pipeline.
/// </summary>
public sealed record PipelineOptions
{
    /// <summary>Applies app-aware formatting to text. Params: text, processName.</summary>
    public Func<string, string?, string>? AppFormatter { get; init; }

    /// <summary>Process name of the target app for formatting.</summary>
    public string? TargetProcessName { get; init; }

    /// <summary>Applies dictionary corrections to text.</summary>
    public Func<string, string>? DictionaryCorrector { get; init; }

    /// <summary>Applies vocabulary boosting to text.</summary>
    public Func<string, string>? VocabularyBooster { get; init; }

    /// <summary>Applies snippet expansion to text.</summary>
    public Func<string, string>? SnippetExpander { get; init; }

    /// <summary>Runs LLM prompt processing on text. Returns processed text.</summary>
    public Func<string, CancellationToken, Task<string>>? LlmHandler { get; init; }

    /// <summary>When true, LLM failures abort processing instead of falling back to the current text.</summary>
    public bool RequireLlmSuccess { get; init; }

    /// <summary>Applies cleanup before prompt actions and snippets.</summary>
    public Func<string, CancellationToken, Task<string>>? CleanupHandler { get; init; }

    /// <summary>
    ///     When true, spoken line-break commands ("new line", "new paragraph")
    ///     are converted to actual line breaks as the very first step, before
    ///     any LLM/cleanup runs. LLMs are unreliable at honoring these verbal
    ///     commands, so we do it deterministically. Enabled for live dictation;
    ///     left off for file transcription (where "new line" is content, not a
    ///     command).
    /// </summary>
    public bool NormalizeSpokenLineBreaks { get; init; }

    /// <summary>
    ///     When true, the unambiguous spoken punctuation phrases "question mark"
    ///     and "exclamation point"/"exclamation mark" are converted to the
    ///     literal <c>?</c>/<c>!</c> symbols early, before the LLM runs. The
    ///     fine-tuned model is intermittent on these and the STT engine only
    ///     sometimes converts them, so a deterministic pass is the reliable
    ///     safety net. Enabled for live dictation; left off for file
    ///     transcription (where the phrase is content, not a command).
    /// </summary>
    public bool NormalizeSpokenPunctuation { get; init; }

    /// <summary>Translates text. Params: text, sourceLang, targetLang. Returns translated text.</summary>
    public Func<
        string,
        string,
        string,
        CancellationToken,
        Task<string>
    >? TranslationHandler { get; init; }

    /// <summary>Translation target language, or null if no translation needed.</summary>
    public string? TranslationTarget { get; init; }

    /// <summary>Effective source language for translation (e.g. "de", "en").</summary>
    public string? EffectiveSourceLanguage { get; init; }

    /// <summary>Detected language from transcription.</summary>
    public string? DetectedLanguage { get; init; }

    /// <summary>Plugin post-processors to include in the pipeline. Each delegate captures its own context.</summary>
    public IReadOnlyList<PluginPostProcessor>? PluginPostProcessors { get; init; }

    /// <summary>Callback to report status updates (e.g. "AI Processing...", "Translating...").</summary>
    public Func<string, Task>? StatusCallback { get; init; }
}

/// <summary>
///     Represents a plugin post-processor with its priority.
///     The delegate captures plugin-specific context (PostProcessingContext) in its closure.
/// </summary>
public sealed record PluginPostProcessor(
    int Priority,
    Func<string, CancellationToken, Task<string>> ProcessAsync
);

/// <summary>
///     Result from the post-processing pipeline.
/// </summary>
public sealed record PostProcessingResult
{
    /// <summary>The fully processed text.</summary>
    public required string Text { get; init; }

    /// <summary>Execution details for each pipeline step that ran.</summary>
    public IReadOnlyList<PostProcessingStepResult> Steps { get; init; } = [];
}

public static class PostProcessingStepNames
{
    public const string SpokenCommands = "SpokenCommands";
    public const string SpokenPunctuation = "SpokenPunctuation";
    public const string Formatting = "Formatting";
    public const string Cleanup = "Cleanup";
    public const string Llm = "LLM";
    public const string Snippets = "Snippets";
    public const string VocabularyBoosting = "VocabularyBoosting";
    public const string Dictionary = "Dictionary";
    public const string Translation = "Translation";

    /// <summary>
    ///     Prefix of the synthesized step name for plugin post-processors, which
    ///     are reported as <c>Plugin(&lt;priority&gt;)</c>.
    /// </summary>
    public const string PluginPrefix = "Plugin(";
}

public sealed record PostProcessingStepResult(
    string Name,
    bool Changed,
    bool Succeeded = true,
    string? ErrorMessage = null
);