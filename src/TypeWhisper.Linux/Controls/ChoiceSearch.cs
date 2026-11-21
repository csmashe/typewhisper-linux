using System.Collections;
using System.Globalization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Controls;

/// <summary>A choice that a <see cref="SearchableComboBox" /> can also find by its code or other names.</summary>
public interface ISearchableChoice
{
    /// <summary>The name shown in the list.</summary>
    string SearchLabel { get; }

    /// <summary>A short code matched by prefix, such as "de".</summary>
    string? SearchCode { get; }

    /// <summary>Other names that find this choice, such as a language's English name.</summary>
    IEnumerable<string> SearchAliases { get; }
}

/// <summary>Filters and ranks the choices of a <see cref="SearchableComboBox" />.</summary>
public static class ChoiceSearch
{
    /// <summary>Lists shorter than this have no search field.</summary>
    public const int Threshold = 10;

    private const CompareOptions Options =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    /// <summary>The choices matching <paramref name="query" />, best first; equal ranks keep the list's order.</summary>
    public static List<object> Filter(IEnumerable items, string query)
    {
        var trimmed = query.Trim();
        var all = items.Cast<object>().Where(item => item is not null);
        if (trimmed.Length == 0)
            return [.. all];
        // Labels follow the interface language, which can differ from the regional format.
        var compare = CultureInfo.CurrentUICulture.CompareInfo;
        return
        [
            .. all.Select(item => (Item: item, Rank: Rank(item, trimmed, compare)))
                .Where(match => match.Rank >= 0)
                .OrderBy(match => match.Rank)
                .Select(match => match.Item),
        ];
    }

    // Translation targets live in Core, so they are mapped here rather than implementing ISearchableChoice.
    private static (string Label, string? Code, IEnumerable<string> Aliases) Describe(object item) => item switch
    {
        ISearchableChoice choice => (choice.SearchLabel, choice.SearchCode, choice.SearchAliases),
        TranslationTargetOption target => (target.DisplayName, target.Code, LanguageAliases(target.Code)),
        _ => (item.ToString() ?? "", null, []),
    };

    // 0 name prefix, 1 code prefix, 2 word prefix, 3 inside a word, -1 no match.
    private static int Rank(object item, string query, CompareInfo compare)
    {
        var (label, code, aliases) = Describe(item);
        string[] names = [label, .. aliases];
        if (names.Any(name => compare.IsPrefix(name, query, Options)))
            return 0;
        if (code is { Length: > 0 } && code.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        return names.Select(name => WordRank(name, query, compare)).Where(rank => rank >= 0).DefaultIfEmpty(-1).Min();
    }

    private static int WordRank(string name, string query, CompareInfo compare)
    {
        var rank = -1;
        for (var index = compare.IndexOf(name, query, Options); index >= 0;
             index = index + 1 < name.Length ? compare.IndexOf(name, query, index + 1, Options) : -1)
        {
            if (index == 0 || !char.IsLetterOrDigit(name[index - 1]))
                return 2;
            rank = 3;
        }
        return rank;
    }

    /// <summary>A language's other names: the list shows its own name (Deutsch), which "ger" would not find.</summary>
    public static IEnumerable<string> LanguageAliases(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return [];
        try
        {
            var culture = CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            // DisplayName follows the interface language.
            return culture.Equals(CultureInfo.InvariantCulture)
                ? []
                : new[] { culture.EnglishName, culture.DisplayName }.Distinct(StringComparer.Ordinal);
        }
        catch (CultureNotFoundException)
        {
            return [];
        }
    }
}
