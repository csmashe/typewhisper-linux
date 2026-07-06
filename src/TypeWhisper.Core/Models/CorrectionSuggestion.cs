namespace TypeWhisper.Core.Models;

/// <summary>
///     A proposed find-and-replace edit to transcribed text (e.g. learned from a
///     user manually correcting a word), carrying a confidence score the UI can
///     rank or threshold on. Distinct from <see cref="DictionaryCorrection" />,
///     which is an already-accepted rule.
/// </summary>
public sealed record CorrectionSuggestion
{
    public CorrectionSuggestion() { }

    public CorrectionSuggestion(string original, string replacement)
    {
        Original = original;
        Replacement = replacement;
    }

    public string Original { get; init; } = "";
    public string Replacement { get; init; } = "";

    /// <summary>Confidence in the suggestion, constrained to [0.0, 1.0]; the setter throws <see cref="ArgumentOutOfRangeException" /> outside that range.</summary>
    public double Confidence
    {
        get;
        init
        {
            if (value is < 0.0 or > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Confidence),
                    value,
                    "Confidence must be between 0.0 and 1.0."
                );
            }

            field = value;
        }
    }
}