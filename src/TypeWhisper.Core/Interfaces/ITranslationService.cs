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
    ///     match or the text is blank.
    /// </summary>
    Task<string> TranslateAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default
    );
}