using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Localization;

/// <summary>
///     The translation-target pickers in the current UI language. Language entries keep their
///     native names; only the "no translation" and "use global setting" choices are translated.
/// </summary>
public static class TranslationTargetLabels
{
    public static IEnumerable<TranslationTargetOption> GlobalOptions() =>
        TranslationModelInfo.GlobalTargetOptions.Select(option =>
            option.Code is null ? option with { DisplayName = Loc.Instance["Common.NoTranslation"] } : option
        );

    // In a profile, null defers to the global setting and "" turns translation off.
    public static IEnumerable<TranslationTargetOption> ProfileOptions() =>
        TranslationModelInfo.ProfileTargetOptions.Select(option => option.Code switch
        {
            null => option with { DisplayName = Loc.Instance["Common.UseGlobalSetting"] },
            "" => option with { DisplayName = Loc.Instance["Common.NoTranslation"] },
            _ => option,
        });
}
