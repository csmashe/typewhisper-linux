namespace TypeWhisper.Core.Models;

/// <summary>
///     An onboarding industry choice that optionally maps to a <see cref="TermPack" />
///     (<paramref name="TermPackId" />, null = none).
///     <see cref="MergeIntoEnabledPackIds" /> folds the preset's pack into the
///     user's enabled set.
/// </summary>
public sealed record IndustryPreset(string Id, string Name, string Description, string? TermPackId)
{
    public static readonly IndustryPreset[] All =
    [
        new(
            "general",
            "General",
            "No industry-specific vocabulary.",
            null
        ),
        new(
            "real-estate",
            "Real Estate",
            "Listings, escrow, financing, and walk-through terms.",
            "real-estate"
        ),
        new(
            "architecture",
            "Architecture",
            "Structural, façade, and design-document terms.",
            "architecture"
        ),
        new(
            "legal",
            "Legal",
            "Contract, compliance, and litigation terms.",
            "legal"
        ),
    ];

    public static string[] MergeIntoEnabledPackIds(string[] enabledPackIds, string presetId)
    {
        var preset = All.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)
        );
        if (preset?.TermPackId is not { } packId || enabledPackIds.Any(id =>
                string.Equals(id, packId, StringComparison.OrdinalIgnoreCase)))
        {
            return enabledPackIds;
        }

        return [.. enabledPackIds, packId];
    }
}
