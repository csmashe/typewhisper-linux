// ReSharper disable UnusedAutoPropertyAccessor.Global

using System.Diagnostics;

namespace TypeWhisper.Core.Models;

/// <summary>
///     Metadata and file manifest for one OPUS-MT translation model (a
///     source→target language pair) hosted as Xenova ONNX exports on Hugging Face.
///     Also the static <see cref="LanguageCatalog" /> (display metadata) and
///     <see cref="AvailableModels" /> (the models that actually exist), with
///     <see cref="FindModel" /> to resolve a pair. The target picker's membership is
///     derived from <see cref="AvailableModels" /> so it can never offer a target no
///     model can produce.
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

    // --- Display catalog (names + badges only) ---

    // How each language is *rendered* (display name, badge) and the order it appears
    // in. This does NOT decide which targets are offered — membership comes from
    // ProducibleTargets below. A code listed here with no model (today: "pl") is
    // simply dropped from the picker; it stays in the catalog ready for the day a
    // model ships. Keep it ordered for the UI.
    private static IReadOnlyList<TranslationLanguage> LanguageCatalog { get; } =
    [
        new("en", "English"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("sv", "Svenska"),
        new("da", "Dansk"),
        new("fi", "Suomi"),
        new("cs", "Čeština"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("hu", "Magyar"),
        new("ja", "日本語"),
        new("zh", "中文"),
        new("ar", "العربية"),
        new("hi", "हिन्दी"),
        new("vi", "Tiếng Việt"),
        new("id", "Bahasa Indonesia")
    ];

    // The OPUS-MT models that actually exist (confirmed Xenova ONNX exports). The
    // single source of truth for which targets the picker may offer. Queried by
    // FindModel and projected into ProducibleTargets. Internal so the guard test can
    // assert the offered targets match the producible ones.
    internal static IReadOnlyList<TranslationModelInfo> AvailableModels { get; } =
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

    // Distinct target languages across every model pair — the targets we can
    // actually produce (directly, or as the en→X leg of the English pivot).
    // Declaration order is preserved so the fallback branch in BuildOptions is
    // deterministic.
    private static readonly IReadOnlyList<string> s_producibleTargets =
        AvailableModels.Select(m => m.TargetLanguage).Distinct().ToList();

    /// <summary>Options list for the Settings (global) ComboBox; first item is "no translation".</summary>
    public static IReadOnlyList<TranslationTargetOption> GlobalTargetOptions { get; } =
        BuildOptions(false);

    /// <summary>Options list for the Profile ComboBox; first item is "use global setting".</summary>
    public static IReadOnlyList<TranslationTargetOption> ProfileTargetOptions { get; } =
        BuildOptions(true);

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
            list.Add(new TranslationTargetOption(null, "Use global setting"));
            list.Add(new TranslationTargetOption("", "No translation"));
        }
        else
        {
            list.Add(new TranslationTargetOption(null, "No translation"));
        }

        // Curated order, but only languages we can actually produce a model for. A
        // catalog entry with no producible model (today: "pl") is dropped here, so
        // the picker can never offer a target that silently no-ops.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var lang in LanguageCatalog)
        {
            if (s_producibleTargets.Contains(lang.Code))
            {
                list.Add(new TranslationTargetOption(lang.Code, lang.DisplayName));
            }
        }

        // Fail-open: a producible target with no catalog entry (a newly added model)
        // stays selectable, rendered with a raw-code fallback, and is flagged so
        // someone adds curated metadata. Appended after the curated block.
        foreach (var code in s_producibleTargets)
        {
            if (LanguageCatalog.Any(l => l.Code == code))
            {
                continue;
            }

            Debug.WriteLine(
                $"TranslationModelInfo: producible target '{code}' has no LanguageCatalog "
                + "entry; showing the raw code. Add a curated entry for a display name."
            );
            list.Add(new TranslationTargetOption(code, code));
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
                    $"{Hf}/opus-mt-{repo}/resolve/main/onnx/encoder_model_quantized.onnx"
                ),
                new TranslationFileInfo(
                    "decoder_model_quantized.onnx",
                    $"{Hf}/opus-mt-{repo}/resolve/main/onnx/decoder_model_quantized.onnx"
                ),
                new TranslationFileInfo("tokenizer.json", $"{Hf}/opus-mt-{repo}/resolve/main/tokenizer.json"),
                new TranslationFileInfo("config.json", $"{Hf}/opus-mt-{repo}/resolve/main/config.json")
            ]
        };
    }
}
