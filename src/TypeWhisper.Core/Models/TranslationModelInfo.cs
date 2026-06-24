// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace TypeWhisper.Core.Models;

/// <summary>
///     Metadata and file manifest for one OPUS-MT translation model (a
///     source→target language pair) hosted as Xenova ONNX exports on Hugging Face.
///     Also the static catalog of <see cref="SupportedLanguages" /> and
///     <see cref="AvailableModels" />, with <see cref="FindModel" /> to resolve a
///     pair.
/// </summary>
public sealed record TranslationModelInfo
{
    // Base URL for Xenova ONNX exports on Hugging Face
    private const string Hf = "https://huggingface.co/Xenova";
    public required string Id { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<TranslationFileInfo> Files { get; init; }
    public required string SubDirectory { get; init; }

    // --- Supported target languages ---

    private static IReadOnlyList<TranslationLanguage> SupportedLanguages { get; } =
    [
        new("en", "English", "EN"),
        new("de", "Deutsch", "DE"),
        new("fr", "Français", "FR"),
        new("es", "Español", "ES"),
        new("it", "Italiano", "IT"),
        new("nl", "Nederlands", "NL"),
        new("pl", "Polski", "PL"),
        new("sv", "Svenska", "SV"),
        new("da", "Dansk", "DA"),
        new("fi", "Suomi", "FI"),
        new("cs", "Čeština", "CS"),
        new("ru", "Русский", "RU"),
        new("uk", "Українська", "UA"),
        new("hu", "Magyar", "HU"),
        new("ja", "日本語", "JP"),
        new("zh", "中文", "CN"),
        new("ar", "العربية", "AR"),
        new("hi", "हिन्दी", "HI"),
        new("vi", "Tiếng Việt", "VN"),
        new("id", "Bahasa Indonesia", "ID")
    ];

    /// <summary>Options list for the Settings (global) ComboBox; first item is "no translation".</summary>
    public static IReadOnlyList<TranslationTargetOption> GlobalTargetOptions { get; } =
        BuildOptions(false);

    /// <summary>Options list for the Profile ComboBox; first item is "use global setting".</summary>
    public static IReadOnlyList<TranslationTargetOption> ProfileTargetOptions { get; } =
        BuildOptions(true);

    private static IReadOnlyList<TranslationModelInfo> AvailableModels { get; } =
    [
        // X → EN (confirmed Xenova exports)
        Pair("de", "en"),
        Pair("fr", "en"),
        Pair("es", "en"),
        Pair("it", "en"),
        Pair("nl", "en"),
        Pair("pl", "en"),
        Pair("sv", "en"),
        Pair("da", "en"),
        Pair("fi", "en"),
        Pair("cs", "en"),
        Pair("ru", "en"),
        Pair("ja", "en"),
        Pair("zh", "en"),
        Pair("ar", "en"),
        Pair("tr", "en"),
        Pair("ko", "en"),
        Pair("hi", "en"),
        Pair("vi", "en"),
        Pair("id", "en"),
        Pair("th", "en"),
        // EN → X (confirmed Xenova exports)
        Pair("en", "de"),
        Pair("en", "fr"),
        Pair("en", "es"),
        Pair("en", "it"),
        Pair("en", "nl"),
        Pair("en", "sv"),
        Pair("en", "da"),
        Pair("en", "fi"),
        Pair("en", "cs"),
        Pair("en", "ru"),
        Pair("en", "zh"),
        Pair("en", "ar"),
        Pair("en", "ja", "en-jap"), // Xenova repo slug uses "jap", not the BCP-47 "ja"
        Pair("en", "hi"),
        Pair("en", "vi"),
        Pair("en", "uk"),
        Pair("en", "hu"),
        Pair("en", "id"),
        // Direct non-English pairs
        Pair("de", "es")
    ];

    public static TranslationModelInfo? FindModel(string sourceLang, string targetLang)
    {
        return AvailableModels.FirstOrDefault(m =>
            m.SourceLanguage == sourceLang && m.TargetLanguage == targetLang
        );
    }

    private static List<TranslationTargetOption> BuildOptions(bool includeGlobal)
    {
        var list = new List<TranslationTargetOption>();

        if (includeGlobal)
        {
            list.Add(new TranslationTargetOption(null, "Use global setting", ""));
            list.Add(new TranslationTargetOption("", "No translation", ""));
        }
        else
        {
            list.Add(new TranslationTargetOption(null, "No translation", ""));
        }

        foreach (var lang in SupportedLanguages)
        {
            list.Add(new TranslationTargetOption(lang.Code, lang.DisplayName, lang.BadgeCode));
        }

        return list;
    }

    // All entries are confirmed ONNX quantized exports from the Xenova organisation on HuggingFace.

    private static TranslationModelInfo Pair(string src, string tgt, string? repoOverride = null)
    {
        var repo = repoOverride ?? $"{src}-{tgt}";
        return new TranslationModelInfo
        {
            Id = $"opus-mt-{repo}",
            SourceLanguage = src,
            TargetLanguage = tgt,
            DisplayName = $"{src}→{tgt}",
            SubDirectory = $"translation-{src}-{tgt}",
            Files =
            [
                new TranslationFileInfo(
                    "encoder_model_quantized.onnx",
                    $"{Hf}/opus-mt-{repo}/resolve/main/onnx/encoder_model_quantized.onnx",
                    50
                ),
                new TranslationFileInfo(
                    "decoder_model_quantized.onnx",
                    $"{Hf}/opus-mt-{repo}/resolve/main/onnx/decoder_model_quantized.onnx",
                    54
                ),
                new TranslationFileInfo("tokenizer.json", $"{Hf}/opus-mt-{repo}/resolve/main/tokenizer.json", 2),
                new TranslationFileInfo("config.json", $"{Hf}/opus-mt-{repo}/resolve/main/config.json", 1)
            ]
        };
    }
}