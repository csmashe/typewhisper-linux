using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Builds the <c>prompt</c> argument for a transcription call. Engines with
///     <see cref="ITranscriptionEngineRole.SupportsStructuredDictionaryTerms" /> get the dictionary
///     as an envelope wherever they run; every other engine keeps the plain-text prompt, which carries
///     comma-separated terms only on the HTTP API path.
/// </summary>
internal static class TranscriptionPromptComposer
{
    /// <summary>Dictation, live preview, streaming sessions, retries and file transcription have no prompt text.</summary>
    public static string? ForDictation(ITranscriptionEngineRole role, IReadOnlyList<string> dictionaryTerms) =>
        role.SupportsStructuredDictionaryTerms ? Structured(role, null, dictionaryTerms) : null;

    /// <summary>The HTTP API adds the caller's prompt text, already merged with any language-hint sentence.</summary>
    public static string? ForApi(
        ITranscriptionEngineRole role,
        string? promptText,
        IReadOnlyList<string> dictionaryTerms
    ) =>
        role.SupportsStructuredDictionaryTerms
            ? Structured(role, promptText, dictionaryTerms)
            : MergeText(
                promptText,
                PluginDictionaryTerms.CreatePrompt(
                    dictionaryTerms,
                    role.DictionaryTermsBudget ?? DictionaryTermsBudget.Default
                )
            );

    public static string? MergeText(params string?[] parts)
    {
        var merged = string.Join(
            Environment.NewLine,
            parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim())
        );
        return string.IsNullOrWhiteSpace(merged) ? null : merged;
    }

    // The budget applies to the terms themselves, so JSON escaping never costs vocabulary.
    private static string? Structured(
        ITranscriptionEngineRole role,
        string? promptText,
        IReadOnlyList<string> dictionaryTerms
    ) =>
        PluginTranscriptionPrompt.Encode(
            promptText,
            PluginDictionaryTerms.Clip(dictionaryTerms, role.DictionaryTermsBudget ?? DictionaryTermsBudget.Default)
        );
}
