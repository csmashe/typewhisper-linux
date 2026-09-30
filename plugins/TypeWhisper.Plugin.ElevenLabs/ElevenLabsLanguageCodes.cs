using System.Globalization;

namespace TypeWhisper.Plugin.ElevenLabs;

// ElevenLabs reports ISO 639-3 codes ("deu", "eng"); the rest of the app keys language
// handling on ISO 639-1, so known three-letter codes are folded and unknown ones kept.
internal static class ElevenLabsLanguageCodes
{
    private static readonly Dictionary<string, string> s_threeToTwo = BuildMap();

    internal static string? Canonicalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var trimmed = code.Trim();
        return trimmed.Length == 3 && s_threeToTwo.TryGetValue(trimmed, out var two) ? two : trimmed;
    }

    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.ThreeLetterISOLanguageName.Length == 3 && culture.TwoLetterISOLanguageName.Length == 2)
                map.TryAdd(culture.ThreeLetterISOLanguageName, culture.TwoLetterISOLanguageName);
        }

        // Macrolanguage codes ICU does not list as neutral cultures.
        map.TryAdd("cmn", "zh");
        map.TryAdd("zho", "zh");
        return map;
    }
}
