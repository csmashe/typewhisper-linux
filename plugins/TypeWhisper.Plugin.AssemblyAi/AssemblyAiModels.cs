using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

internal sealed record AssemblyAiModel(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Languages,
    DictionaryTermsBudget Budget
)
{
    internal bool IsPro => Id == AssemblyAiModels.DefaultId;
}

internal static class AssemblyAiModels
{
    internal const string DefaultId = "universal-3-5-pro";
    private const string Universal2Id = "universal-2";
    // No longer accepted by the API; saved selections move to its successor.
    private const string RetiredProId = "universal-3-pro";

    // AssemblyAI's recommended real-time model; covers every Universal-3.5 Pro language.
    internal const string ProStreamingModelId = "universal-3-6-pro";
    private static readonly IReadOnlyList<string> s_multilingualStreamingLanguages = ["en", "es", "de", "fr", "pt", "it"];

    // Recorded audio counts each word of a phrase toward the term limit, so KeytermsFor caps total words.
    internal static IReadOnlyList<AssemblyAiModel> All { get; } =
    [
        new(
            DefaultId,
            "Universal-3.5 Pro",
            ["en", "es", "fr", "de", "it", "pt", "ar", "da", "nl", "fi", "he", "hi", "ja", "zh", "no", "sv", "tr", "vi"],
            new DictionaryTermsBudget(MaxTerms: 1000, MaxWordsPerTerm: 6)
        ),
        new(
            Universal2Id,
            "Universal-2",
            [
                "en", "es", "fr", "de", "it", "pt", "nl", "hi", "ja", "zh", "fi", "ko", "pl", "ru", "tr", "uk", "vi",
                "af", "sq", "am", "ar", "hy", "as", "az", "ba", "eu", "be", "bn", "bs", "br", "bg", "my", "ca", "hr",
                "cs", "da", "et", "fo", "gl", "ka", "el", "gu", "ht", "ha", "haw", "he", "hu", "is", "id", "jw", "kn",
                "kk", "km", "lo", "la", "lv", "ln", "lt", "lb", "mk", "mg", "ms", "ml", "mt", "mi", "mr", "mn", "ne",
                "no", "nn", "oc", "pa", "ps", "fa", "ro", "sa", "sr", "sn", "sd", "si", "sk", "sl", "so", "su", "sw",
                "sv", "tl", "tg", "ta", "tt", "te", "th", "bo", "tk", "ur", "uz", "cy", "yi", "yo",
            ],
            new DictionaryTermsBudget(MaxTerms: 200, MaxWordsPerTerm: 6)
        ),
    ];

    internal static AssemblyAiModel? Find(string? id) =>
        All.FirstOrDefault(model => model.Id == (id == RetiredProId ? DefaultId : id));

    internal static AssemblyAiModel Resolve(string? id) => Find(id) ?? All[0];

    internal static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : language.Trim().ToLowerInvariant();

    internal static bool IsEnglish(string language) =>
        language == "en" || language.StartsWith("en-", StringComparison.Ordinal) || language.StartsWith("en_", StringComparison.Ordinal);

    // Universal-Streaming covers six languages; Auto and other languages use recorded audio.
    internal static bool SupportsStreamingLanguage(AssemblyAiModel model, string? language) =>
        model.IsPro
        || (NormalizeLanguage(language) is { } code
            && (IsEnglish(code) || s_multilingualStreamingLanguages.Contains(BaseLanguage(code))));

    internal static string BaseLanguage(string language) => language.Split('-', '_')[0];
}
