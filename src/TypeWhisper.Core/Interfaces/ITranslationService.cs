using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Translates transcribed text between languages, downloading and loading the
///     required model on demand (or delegating to a configured LLM provider).
/// </summary>
public interface ITranslationService
{
    /// <summary>
    ///     Translates <paramref name="text" /> from <paramref name="sourceLang" /> to
    ///     <paramref name="targetLang" />. Returns the input unchanged when the languages
    ///     match or the text is blank. When the configured provider is an LLM (cloud or
    ///     local), the call is recorded to <paramref name="capture" /> so the history
    ///     Inspect panel can show what left the machine.
    /// </summary>
    Task<string> TranslateAsync(
        string text,
        string sourceLang,
        string targetLang,
        LlmCallCapture? capture = null,
        CancellationToken ct = default
    );
}