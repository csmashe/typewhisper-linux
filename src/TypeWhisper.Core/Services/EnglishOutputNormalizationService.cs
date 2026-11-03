using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>Applies the chosen regional spelling to final English output.</summary>
public static partial class EnglishOutputNormalizationService
{
    private const string ResourcePrefix = "TypeWhisper.Core.Resources.EnglishSpelling";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_americanSpellings =
        new(() => LoadSpellings("american.tsv"));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_britishSpellings =
        new(() => LoadSpellings("british.tsv"));

    /// <summary>Respells <paramref name="text" /> when the resolved output language is English.</summary>
    public static string NormalizeText(
        string text,
        EnglishOutputVariant variant,
        TranscriptionTask transcriptionTask = TranscriptionTask.Transcribe,
        string? detectedLanguage = null,
        string? configuredLanguage = null,
        IReadOnlyList<string>? configuredLanguageCandidates = null,
        string? translationTarget = null)
    {
        if (variant == EnglishOutputVariant.AsTranscribed
            || string.IsNullOrEmpty(text)
            || !TranscriptionOutputLanguageResolver.IsOutputLanguage(
                "en",
                transcriptionTask,
                detectedLanguage,
                configuredLanguage,
                configuredLanguageCandidates ?? [],
                translationTarget))
        {
            return text;
        }

        var spellings = variant switch
        {
            EnglishOutputVariant.UnitedStates => s_americanSpellings.Value,
            EnglishOutputVariant.UnitedKingdom => s_britishSpellings.Value,
            _ => null,
        };

        return spellings is null
            ? text
            : OutputLiteralTokens.RespellProse(
                text,
                token => EnglishWord().Replace(token, match => NormalizeWord(match.Value, spellings)));
    }

    /// <summary>Respells a transcription's text and segments together.</summary>
    public static TranscriptionResult NormalizeResult(
        TranscriptionResult result,
        EnglishOutputVariant variant,
        TranscriptionTask transcriptionTask = TranscriptionTask.Transcribe,
        string? configuredLanguage = null,
        IReadOnlyList<string>? configuredLanguageCandidates = null)
    {
        var candidates = configuredLanguageCandidates ?? [];
        return result with
        {
            Text = NormalizeText(
                result.Text,
                variant,
                transcriptionTask,
                result.DetectedLanguage,
                configuredLanguage,
                candidates),
            Segments = result.Segments
                .Select(segment => segment with
                {
                    Text = NormalizeText(
                        segment.Text,
                        variant,
                        transcriptionTask,
                        result.DetectedLanguage,
                        configuredLanguage,
                        candidates),
                })
                .ToList(),
        };
    }

    private static string NormalizeWord(string word, IReadOnlyDictionary<string, string> spellings)
    {
        var lookupWord = word.Replace('’', '\'').ToLowerInvariant();
        if (!spellings.TryGetValue(lookupWord, out var replacement))
        {
            return word;
        }

        replacement = PreserveCase(word, replacement);
        return word.Contains('’', StringComparison.Ordinal)
            ? replacement.Replace('\'', '’')
            : replacement;
    }

    private static string PreserveCase(string source, string replacement)
    {
        if (source.All(static character => !char.IsLetter(character) || char.IsUpper(character)))
        {
            return replacement.ToUpperInvariant();
        }

        if (char.IsUpper(source[0])
            && source.Skip(1).All(static character => !char.IsLetter(character) || char.IsLower(character)))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return source.All(static character => !char.IsLetter(character) || char.IsLower(character))
            ? replacement
            : source;
    }

    private static Dictionary<string, string> LoadSpellings(string fileName)
    {
        var resourceName = $"{ResourcePrefix}.{fileName}";
        using var stream = typeof(EnglishOutputNormalizationService).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded spelling resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            spellings[line[..separator]] = line[(separator + 1)..];
        }

        return spellings;
    }

    [GeneratedRegex(@"(?<![\p{L}\p{M}])[\p{L}\p{M}]+(?:['’][\p{L}\p{M}]+)*(?![\p{L}\p{M}])", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWord();
}
