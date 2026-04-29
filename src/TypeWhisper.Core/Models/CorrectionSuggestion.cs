namespace TypeWhisper.Core.Models;

public sealed record CorrectionSuggestion
{
    private double _confidence;

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
    public double Confidence
    {
        get => _confidence;
        init
        {
            if (value is < 0.0 or > 1.0)
                throw new ArgumentOutOfRangeException(nameof(Confidence), value, "Confidence must be between 0.0 and 1.0.");

            _confidence = value;
        }
    }
}
