using System.Globalization;

namespace TypeWhisper.Plugin.OpenRouter;

internal sealed record OpenRouterFetchedModel(
    string Id,
    string Name,
    string PromptPrice,
    string CompletionPrice)
{
    public string FormattedPricing(string freeLabel)
    {
        var promptPer1M = ParsePrice(PromptPrice) * 1_000_000;
        var completionPer1M = ParsePrice(CompletionPrice) * 1_000_000;

        if (Math.Abs(promptPer1M) < 1e-9 && Math.Abs(completionPer1M) < 1e-9)
            return freeLabel;

        return FormattableString.Invariant($"${promptPer1M:0.00}/${completionPer1M:0.00} per 1M");
    }

    private static double ParsePrice(string? value) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : 0;
}
