using System.Text.Json;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>
///     The saved text-model catalog. Version 2 lists one entry per model, with every alias Mistral
///     lists for it in <see cref="Aliases" />; older versions saved the raw <c>/v1/models</c> IDs.
/// </summary>
internal sealed record MistralTextModelCatalog(
    string? Owner,
    List<string>? Models,
    int Version = 0,
    Dictionary<string, string>? Names = null,
    Dictionary<string, string>? Aliases = null);

/// <summary>Mistral's current model lineup and the cleanup applied to an account's <c>/v1/models</c>.</summary>
internal static class MistralCatalog
{
    internal const int CurrentVersion = 2;

    internal const string MiniTranscribe = "voxtral-mini-transcribe-3-0";
    internal const string SmallTranscribe = "voxtral-small-transcribe-3-0";

    private const string SmallText = "mistral-small-2603";
    private const string MediumText = "mistral-medium-2604";
    private const string LargeText = "mistral-large-4";

    /// <summary>Languages Mistral accepted for Voxtral Transcribe 3 on 2026-11-06 (upstream typewhisper-win #754).</summary>
    internal static string[] Voxtral3Languages { get; } =
    [
        "ar", "en", "de", "es", "fr", "hi", "it", "nl", "pt", "zh", "ru", "ko", "ja", "id", "tr", "vi", "sv", "pl", "th", "tl",
        "no", "da", "hu", "cs", "uk", "ro", "ms", "ca", "el", "sk", "fi", "lv", "km", "pa", "ta", "my", "bg", "mr", "ur", "et",
        "te", "gu", "kn", "fa", "sl", "ml", "is", "bn", "hr", "mk", "eu", "sr", "ps", "lb", "ga", "or", "lt", "mt",
    ];

    /// <summary>
    ///     Saved transcription selections from earlier versions: Voxtral 2 IDs, the moving
    ///     <c>voxtral-mini-latest</c> alias, the pre-rename ID and the short Transcribe 3 aliases.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> TranscriptionReplacements { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mistral-whisper"] = MiniTranscribe,
            ["voxtral-mini-latest"] = MiniTranscribe,
            ["voxtral-mini-2507"] = MiniTranscribe,
            ["voxtral-mini-2602"] = MiniTranscribe,
            ["voxtral-mini-transcribe-3"] = MiniTranscribe,
            ["voxtral-mini-transcribe-latest"] = MiniTranscribe,
            ["voxtral-small-transcribe-3"] = SmallTranscribe,
            ["voxtral-small-transcribe-latest"] = SmallTranscribe,
        };

    /// <summary>Product names and order for known text models; the default leads because blank requests take the first.</summary>
    private static readonly (string Id, string Name)[] s_knownText =
    [
        (SmallText, "Mistral Small 4"),
        (MediumText, "Mistral Medium 3.5"),
        (LargeText, "Mistral Large 4"),
        ("ministral-14b-2512", "Ministral 3 14B"),
        ("ministral-8b-2512", "Ministral 3 8B"),
        ("ministral-3b-2512", "Ministral 3 3B"),
    ];

    /// <summary>
    ///     Earlier chat models and their successor (upstream's list, matching the macOS plugin). A dated
    ///     key's model is hidden while its successor is listed.
    /// </summary>
    private static readonly Dictionary<string, string> s_textReplacements =
        new(StringComparer.Ordinal)
        {
            ["mistral-large-latest"] = LargeText,
            ["mistral-large-2512"] = LargeText,
            ["mistral-large-2411"] = LargeText,
            ["mistral-large-2407"] = LargeText,
            ["mistral-large-2402"] = LargeText,
            ["mistral-small-2506"] = SmallText,
            ["mistral-small-2503"] = SmallText,
            ["mistral-small-2501"] = SmallText,
            ["mistral-small-2409"] = SmallText,
            ["mistral-small-2402"] = SmallText,
            ["pixtral-12b-2409"] = "ministral-14b-2512",
            ["pixtral-large-2411"] = "ministral-14b-2512",
            ["pixtral-large-latest"] = "ministral-14b-2512",
            ["ministral-8b-2410"] = "ministral-8b-2512",
            ["ministral-3b-2410"] = "ministral-3b-2512",
        };

    /// <summary>Aliases of the default lineup, used until the account catalog is loaded.</summary>
    internal static IReadOnlyDictionary<string, string> DefaultTextAliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mistral-small-latest"] = SmallText,
            ["mistral-medium-latest"] = MediumText,
            ["mistral-medium"] = MediumText,
            ["mistral-medium-3"] = MediumText,
            ["mistral-medium-3-5"] = MediumText,
            ["mistral-medium-3.5"] = MediumText,
            ["mistral-large-4-0"] = LargeText,
            ["ministral-14b-latest"] = "ministral-14b-2512",
            ["ministral-8b-latest"] = "ministral-8b-2512",
            ["ministral-3b-latest"] = "ministral-3b-2512",
        };

    internal static string[] DefaultTextModels { get; } = s_knownText.Select(k => k.Id).ToArray();

    /// <summary>The default lineup earlier versions offered, mapped to the models that replace it.</summary>
    internal static IReadOnlyDictionary<string, string> FormerDefaultTextModels { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mistral-small-latest"] = SmallText,
            ["mistral-medium-latest"] = MediumText,
            ["mistral-large-latest"] = LargeText,
        };

    /// <summary>
    ///     Reasoning levels Mistral documents per model, fastest first. Large 4 rejects <c>none</c>;
    ///     Ministral and models without reasoning reject the parameter, so they get no levels.
    /// </summary>
    internal static string[] ReasoningEfforts(string modelId) => modelId switch
    {
        LargeText => ["low", "medium", "high"],
        SmallText or MediumText => ["none", "high"],
        _ => [],
    };

    /// <summary>
    ///     Picker position: known models by their lineup order, also when listed under an alias or a
    ///     retired ID they replace; other models after them.
    /// </summary>
    internal static int Rank(string id, IReadOnlyDictionary<string, string> aliases)
    {
        foreach (var candidate in new[] { id, aliases.GetValueOrDefault(id), s_textReplacements.GetValueOrDefault(id) })
        {
            var index = candidate is null ? -1 : Array.FindIndex(s_knownText, k => k.Id == candidate);
            if (index >= 0)
                return index;
        }

        return int.MaxValue;
    }

    internal static string DisplayName(string id, IReadOnlyDictionary<string, string>? names) =>
        Array.Find(s_knownText, k => k.Id == id).Name ?? names?.GetValueOrDefault(id) ?? id;

    /// <summary>
    ///     Maps a saved, alias or retired model ID to an ID in <paramref name="listed" />, or null when
    ///     none matches.
    /// </summary>
    internal static string? Resolve(
        string? saved, IReadOnlyCollection<string> listed, IReadOnlyDictionary<string, string> aliases)
    {
        if (string.IsNullOrWhiteSpace(saved))
            return null;
        var raw = saved.Trim();
        if (listed.Contains(raw))
            return raw;
        var id = aliases.GetValueOrDefault(raw, raw);
        if (listed.Contains(id))
            return id;
        if (!s_textReplacements.TryGetValue(raw, out var replacement) && !s_textReplacements.TryGetValue(id, out replacement))
            return null;

        replacement = aliases.GetValueOrDefault(replacement, replacement);
        return listed.Contains(replacement) ? replacement : null;
    }

    /// <summary>
    ///     Groups each model with its aliases, keeps general chat models, hides models a listed successor
    ///     replaces and orders known models first. Returns null for a malformed or oversized list, or one
    ///     without text models, so a partial catalog is never saved.
    /// </summary>
    internal static MistralTextModelCatalog? ParseText(string body, string owner, int maxModels)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0
                || data.GetArrayLength() > maxModels)
            {
                return null;
            }

            var entries = new List<Entry>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !IsModelId(idElement.GetString())
                    || !item.TryGetProperty("capabilities", out var capabilities)
                    || capabilities.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (IsTrue(item, "archived"))
                    continue;

                var aliases = item.TryGetProperty("aliases", out var rawAliases) && rawAliases.ValueKind == JsonValueKind.Array
                    ? rawAliases.EnumerateArray()
                        .Where(a => a.ValueKind == JsonValueKind.String && IsModelId(a.GetString()))
                        .Select(a => a.GetString()!)
                        .ToArray()
                    : [];
                var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                // Text processing needs a general chat model: no speech, audio, code completion or Labs models.
                var isText = IsTrue(capabilities, "completion_chat")
                             && !IsTrue(capabilities, "audio_speech")
                             && !IsTrue(capabilities, "audio")
                             && !IsTrue(capabilities, "completion_fim");
                entries.Add(new Entry(idElement.GetString()!, name, aliases, isText));
            }

            return Build(entries, owner);
        }
    }

    private static MistralTextModelCatalog? Build(List<Entry> entries, string owner)
    {
        // Mistral lists every alias as its own model; aliases of one model form a group.
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            Union(entry.Id, entry.Id);
            foreach (var alias in entry.Aliases)
                Union(entry.Id, alias);
        }

        var members = parent.Keys.ToArray()
            .GroupBy(Find, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var models = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(e => Find(e.Id), StringComparer.Ordinal))
        {
            var groupMembers = members[group.Key];
            var apiName = group.Select(e => e.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();
            var id = Canonical(groupMembers, apiName);
            var source = group.FirstOrDefault(e => e.Id == id) ?? group.First();
            if (!source.IsText || groupMembers.Any(m => m.StartsWith("labs-", StringComparison.Ordinal)))
                continue;

            foreach (var member in groupMembers.Where(m => m != id))
                aliasMap[member] = id;
            models.Add(id);
            if (apiName is not null && !groupMembers.Contains(apiName, StringComparer.Ordinal))
                names[id] = apiName;
        }

        var listed = models.ToHashSet(StringComparer.Ordinal);

        // A hidden model and every alias of it resolve to the successor that replaces it.
        var successors = models
            .Select(m => (Model: m, Successor: SuccessorOf(m)))
            .Where(pair => pair.Successor is not null)
            .ToDictionary(pair => pair.Model, pair => pair.Successor!, StringComparer.Ordinal);
        foreach (var (alias, target) in aliasMap.ToArray())
        {
            if (successors.TryGetValue(target, out var successor))
                aliasMap[alias] = successor;
        }

        foreach (var (hidden, successor) in successors)
            aliasMap[hidden] = successor;

        var ordered = models
            .Where(m => !successors.ContainsKey(m))
            .OrderBy(m => Rank(m, DefaultTextAliases))
            .ThenBy(m => DisplayName(m, names), StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count == 0)
            return null;

        var kept = ordered.ToHashSet(StringComparer.Ordinal);
        return new MistralTextModelCatalog(
            owner,
            ordered,
            CurrentVersion,
            names.Where(pair => kept.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal),
            aliasMap);

        string Find(string id)
        {
            while (parent[id] != id)
                id = parent[id] = parent[parent[id]];
            return id;
        }

        void Union(string a, string b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            var (rootA, rootB) = (Find(a), Find(b));
            if (rootA != rootB)
                parent[rootB] = rootA;
        }

        string CanonicalOf(string value) => aliasMap.GetValueOrDefault(value, value);

        // A "-latest" key may already point at a newer model than its successor; the dated keys name the old ones.
        string? SuccessorOf(string modelId) => s_textReplacements
            .Where(pair => !pair.Key.EndsWith("-latest", StringComparison.Ordinal) && CanonicalOf(pair.Key) == modelId)
            .Select(pair => CanonicalOf(pair.Value))
            .FirstOrDefault(successor => successor != modelId && listed.Contains(successor));
    }

    // Known IDs win, then the ID the catalog names the model by, then the most specific ID. A "-latest"
    // alias moves to the next release, so it is used only when the model has no other ID.
    private static string Canonical(string[] members, string? apiName)
    {
        foreach (var (id, _) in s_knownText)
        {
            if (members.Contains(id, StringComparer.Ordinal))
                return id;
        }

        if (apiName is not null
            && !apiName.EndsWith("-latest", StringComparison.Ordinal)
            && members.Contains(apiName, StringComparer.Ordinal))
        {
            return apiName;
        }

        return members
            .OrderBy(i => i.EndsWith("-latest", StringComparison.Ordinal))
            .ThenByDescending(i => i.Length)
            .ThenBy(i => i, StringComparer.Ordinal)
            .First();
    }

    internal static bool IsModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 256
        && !id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed record Entry(string Id, string? Name, string[] Aliases, bool IsText);
}
