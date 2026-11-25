using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
///     Guesses the language of dictated text, for dictations that report none. It is deliberately
///     strict: an answer needs several matching function words and a clear lead over every other
///     language, so in doubt there is none.
/// </summary>
internal static partial class TextLanguageDetector
{
    private const int MinimumHits = 3;
    private const int MinimumLeadFactor = 3;

    // Function words per language. A word listed for more than one language says nothing about
    // either and is ignored. Default filler words must not appear: "um" would vote for English in
    // the very German sentence it should be kept in. Languages without default fillers (fr, pt,
    // it, nl, uk) are listed so their text does not pass for a neighbour that has some.
    private static readonly Dictionary<string, string> s_languageByWord = BuildIndex(new Dictionary<string, string>
    {
        ["en"] = "the and is are that this it i you we they she with for not have has been be does did what think " +
                 "just can would your there about but if know like going get don't it's i'm that's let's yeah really " +
                 "because should could which when where who how we'll i've i'll",
        ["de"] = "der die das und ist nicht ich du wir sie er es ein eine einen einem einer zu mit auf für den dem des " +
                 "von sich auch noch aber dass wenn uhr schon jetzt habe haben hat bin sind war waren gut heute morgen " +
                 "nur oder kann können wird werden mir mich dir uns euch ihr im zum zur beim nach bei aus über sehr doch " +
                 "ja nein nichts etwas wer warum weil dann also diese dieser dieses gibt geht machen komme kommt",
        ["es"] = "el los las y es no un una por para con lo su al del está pero muy yo tú él ella también hay sí más " +
                 "cuando esto eso estoy tengo hace",
        ["fr"] = "le la les et est une des du que qui je il elle nous vous ils elles pas ne ce cette dans sur pour avec " +
                 "mais où oui très c'est j'ai suis au aux être avoir fait sont",
        // Not "em" ("in"): it is a Spanish default filler.
        ["pt"] = "os não na com para por eu você ele ela nós isso isto muito foi está estou tem ao já mais também " +
                 "ontem hoje uma no",
        ["it"] = "il lo la gli le è di che un una per con sono ho ha ma anche questo questa molto perché io lui lei noi " +
                 "voi loro del della nel nella",
        ["nl"] = "de het een is van dat niet ik je we wij zij hij met op voor maar ook nog wat zijn heb heeft er naar om " +
                 "bij uit wel geen",
        ["ru"] = "и в не на я что он с это как а то все она так его но ты к у же вы за бы по только мне было вот от " +
                 "меня еще нет из ему когда даже был если уже или ничего быть была очень сейчас тоже мы они хорошо",
        ["uk"] = "і й що це та але як ми ви він вона воно вони ще вже тут там дуже мені його її є був була було " +
                 "щоб коли також чи цей ця",
    });

    /// <summary>
    ///     Returns the two-letter code of the language <paramref name="text"/> is written in, or null
    ///     when the text does not show it reliably.
    /// </summary>
    internal static string? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Kana letters are written only in Japanese, and Japanese has no spaces to split function words on.
        if (text.Count(static c => char.IsLetter(c) && c is >= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF') >= MinimumHits)
        {
            return "ja";
        }

        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (Match match in WordPattern().Matches(text))
            {
                if (s_languageByWord.TryGetValue(match.Value.Replace('’', '\'').ToLowerInvariant(), out var language))
                {
                    hits[language] = hits.GetValueOrDefault(language) + 1;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        string? best = null;
        var bestHits = 0;
        var runnerUpHits = 0;
        foreach (var (language, count) in hits)
        {
            if (count > bestHits)
            {
                runnerUpHits = bestHits;
                best = language;
                bestHits = count;
            }
            else if (count > runnerUpHits)
            {
                runnerUpHits = count;
            }
        }

        return bestHits >= MinimumHits && bestHits >= runnerUpHits * MinimumLeadFactor ? best : null;
    }

    private static Dictionary<string, string> BuildIndex(Dictionary<string, string> wordsByLanguage) =>
        wordsByLanguage
            .SelectMany(static pair => pair.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .Select(word => (Word: word, Language: pair.Key)))
            .GroupBy(static entry => entry.Word, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single().Language, StringComparer.Ordinal);

    [GeneratedRegex(@"\p{L}+(?:['’]\p{L}+)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex WordPattern();
}
