using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

/// <summary>
///     Guards the translation target picker: the offered targets
///     (<see cref="TranslationModelInfo.GlobalTargetOptions" /> /
///     <see cref="TranslationModelInfo.ProfileTargetOptions" />) must stay exactly in
///     step with the targets the model catalog can actually produce. This is the test
///     that would have caught the "Polish offered but unproducible" drift.
/// </summary>
public class TranslationModelInfoTests
{
    // Distinct targets the model catalog can produce — the source of truth.
    private static List<string> ProducibleTargets() =>
        TranslationModelInfo.AvailableModels
            .Select(m => m.TargetLanguage)
            .Distinct()
            .ToList();

    // Real (non-sentinel) target codes a picker offers. The "use global" / "no
    // translation" rows carry a null or empty Code and are excluded.
    private static List<string> OfferedTargets(IEnumerable<TranslationTargetOption> options) =>
        options.Select(o => o.Code)
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .ToList();

    private static void AssertOfferedMatchesProducible(IReadOnlyList<TranslationTargetOption> options)
    {
        // Both directions at once: nothing offered that can't be produced, and
        // nothing producible left silently missing from the picker.
        Assert.Equal(
            ProducibleTargets().OrderBy(c => c, StringComparer.Ordinal),
            OfferedTargets(options).OrderBy(c => c, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void Global_picker_offers_exactly_the_producible_targets() =>
        AssertOfferedMatchesProducible(TranslationModelInfo.GlobalTargetOptions);

    [Fact]
    public void Profile_picker_offers_exactly_the_producible_targets() =>
        AssertOfferedMatchesProducible(TranslationModelInfo.ProfileTargetOptions);

    [Fact]
    public void Polish_is_offered_by_no_picker_because_no_model_produces_it()
    {
        // Regression pin for the original drift: "pl" lives in the display catalog
        // but no *→pl model exists, so selecting it would silently no-op.
        Assert.DoesNotContain("pl", ProducibleTargets());
        Assert.Null(TranslationModelInfo.FindModel("en", "pl"));
        Assert.DoesNotContain("pl", OfferedTargets(TranslationModelInfo.GlobalTargetOptions));
        Assert.DoesNotContain("pl", OfferedTargets(TranslationModelInfo.ProfileTargetOptions));
    }

    [Fact]
    public void Sentinel_rows_are_present_and_carry_no_target_code()
    {
        // OfferedTargets relies on these rows having a null/empty Code so it can skip
        // them. Global leads with "no translation"; Profile with "use global" then
        // "no translation".
        Assert.Null(TranslationModelInfo.GlobalTargetOptions[0].Code);
        Assert.Null(TranslationModelInfo.ProfileTargetOptions[0].Code);
        Assert.Equal("", TranslationModelInfo.ProfileTargetOptions[1].Code);
    }
}
