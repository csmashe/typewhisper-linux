using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Core.Services;

/// <summary>Localization keys for the names of seeded prompts and profiles.</summary>
public static class DefaultItemNames
{
    public const string AutoCleanupAction = "DefaultNames.AutoCleanupAction";
    public const string AutoFormatProfile = "DefaultNames.AutoFormatProfile";
    public const string TranslateToEnglish = "DefaultNames.TranslateToEnglish";
    public const string WriteEmail = "DefaultNames.WriteEmail";
    public const string FormatAsList = "DefaultNames.FormatAsList";
    public const string ActionItems = "DefaultNames.ActionItems";
    public const string Reply = "DefaultNames.Reply";

    private static readonly Dictionary<string, string> s_english = new(StringComparer.Ordinal)
    {
        [AutoCleanupAction] = "Auto Clean Up Text",
        [AutoFormatProfile] = "Auto Format",
        [TranslateToEnglish] = "Translate to English",
        [WriteEmail] = "Write Email",
        [FormatAsList] = "Format as List",
        [ActionItems] = "Action Items",
        [Reply] = "Reply",
    };

    public static IReadOnlyList<string> PresetKeys { get; } =
        [TranslateToEnglish, WriteEmail, FormatAsList, ActionItems, Reply];

    /// <summary>The English names, for hosts that supply no localizer.</summary>
    public static IDefaultNameLocalizer English { get; } = new EnglishNames();

    private sealed class EnglishNames : IDefaultNameLocalizer
    {
        public string GetName(string key) => s_english[key];

        public bool IsShippedName(string key, string name) =>
            s_english.TryGetValue(key, out var english) && english == name;
    }
}
