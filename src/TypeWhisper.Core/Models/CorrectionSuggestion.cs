namespace TypeWhisper.Core.Models;

public sealed record CorrectionSuggestion
{
    public CorrectionSuggestion()
    {
    }

    public CorrectionSuggestion(string original, string replacement)
    {
        Original = original;
        Replacement = replacement;
    }

    public string Original { get; init; } = "";
    public string Replacement { get; init; } = "";
    public double Confidence { get; init; }
}
