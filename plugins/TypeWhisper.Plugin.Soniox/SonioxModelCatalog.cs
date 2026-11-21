using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.Soniox;

internal sealed record SonioxSttModel(string Id, string? AliasedModelId, string Mode);

/// <summary>One region's speech-to-text models, cached so automatic selection works offline.</summary>
internal sealed record SonioxCatalog(string Region, DateTimeOffset FetchedAt, IReadOnlyList<SonioxSttModel> SttModels);

/// <summary>Chooses Soniox models from the cached catalog, with built-in v6 fallbacks when none is cached.</summary>
internal static partial class SonioxModels
{
    internal const string Automatic = "automatic";
    internal const string RealtimeMode = "real_time";
    internal const string AsyncMode = "async";
    private const string RealtimePrefix = "stt-rt-";
    private const string AsyncPrefix = "stt-async-";

    // Soniox retires v5 on 2026-12-15 and routes it to v6.
    internal const string FallbackRealtimeModelId = "stt-rt-v6";
    private const string FallbackAsyncModelId = "stt-async-v6";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex VersionNumbers();

    private static bool IsValidModelId(string? id) => id is not null && ModelIdPattern().IsMatch(id);

    /// <summary>Parses a <c>/v1/models</c> response; throws <see cref="JsonException" /> when it is not a model list.</summary>
    internal static IReadOnlyList<SonioxSttModel> ParseSttModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Soniox returned an invalid model list.");
        }

        var parsed = new List<SonioxSttModel>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
                continue;

            var id = GetString(model, "id");
            var mode = GetString(model, "transcription_mode");
            if (!IsValidModelId(id) || mode is not (RealtimeMode or AsyncMode))
                continue;

            var alias = GetString(model, "aliased_model_id");
            parsed.Add(new SonioxSttModel(id!, IsValidModelId(alias) ? alias : null, mode));
        }

        return parsed.DistinctBy(model => model.Id).ToArray();
    }

    /// <summary>Drops cached entries that a damaged settings file or another plugin version would not accept.</summary>
    internal static SonioxCatalog? Sanitize(SonioxCatalog? catalog)
    {
        // The settings store deserializes JSON, so non-nullable members can still arrive null.
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (catalog?.SttModels is null || string.IsNullOrWhiteSpace(catalog.Region))
            return null;

        var models = catalog.SttModels
            .Where(model => model is not null && IsValidModelId(model.Id) && model.Mode is RealtimeMode or AsyncMode)
            .Select(model => model with { AliasedModelId = IsValidModelId(model.AliasedModelId) ? model.AliasedModelId : null })
            .DistinctBy(model => model.Id)
            .ToArray();
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        return models.Length == 0 ? null : catalog with { SttModels = models };
    }

    /// <summary>
    /// The newest model for a mode. An alias named latest, default or recommended wins; otherwise the highest
    /// version that is neither an alias nor a preview. Previews stay selectable by version but never become automatic.
    /// </summary>
    internal static string PreferredSttModelId(IReadOnlyList<SonioxSttModel>? models, string mode)
    {
        var scoped = (models ?? []).Where(model => model.Mode == mode).ToArray();
        var alias = scoped.FirstOrDefault(model =>
            model.AliasedModelId is not null && IsMovingAlias(model.Id) && !IsPreview(model.Id) && !IsPreview(model.AliasedModelId));
        if (alias is not null)
            return alias.Id;

        return scoped
            .Where(model => model.AliasedModelId is null && !IsPreview(model.Id))
            .Select(model => model.Id)
            .OrderBy(id => id, ModelIdComparer.Instance)
            .LastOrDefault()
            ?? (mode == AsyncMode ? FallbackAsyncModelId : FallbackRealtimeModelId);
    }

    private static bool IsPreview(string id) => id.Contains("preview", StringComparison.Ordinal);

    private static bool IsMovingAlias(string id) =>
        id.Contains("latest", StringComparison.Ordinal)
        || id.Contains("default", StringComparison.Ordinal)
        || id.Contains("recommended", StringComparison.Ordinal);

    /// <summary>The version part shared by a real-time and an async model, such as "v6" for stt-rt-v6 and stt-async-v6.</summary>
    internal static string? VersionKey(string id) =>
        id.StartsWith(RealtimePrefix, StringComparison.Ordinal) ? id[RealtimePrefix.Length..]
        : id.StartsWith(AsyncPrefix, StringComparison.Ordinal) ? id[AsyncPrefix.Length..]
        : null;

    /// <summary>A bare version such as "v6"; a full model id like "stt-rt-v6" is not a version key.</summary>
    private static bool IsValidVersionKey(string? key) =>
        key is { Length: > 0 } && VersionKey(key) is null && IsValidModelId(RealtimePrefix + key);

    /// <summary>
    /// The stored selection as "automatic" or a bare version. A full model id such as "stt-async-v5" becomes "v5",
    /// so no doubled prefix reaches Soniox.
    /// </summary>
    internal static string NormalizeSelection(string? selection)
    {
        var trimmed = selection?.Trim();
        var key = trimmed is null ? null : VersionKey(trimmed) ?? trimmed;
        return IsValidVersionKey(key) ? key! : Automatic;
    }

    /// <summary>
    /// Versions offered in settings, newest first: those the region lists, not as an alias, for both live text and
    /// recordings, because a pin applies to both.
    /// </summary>
    internal static IReadOnlyList<string> SelectableVersions(IReadOnlyList<SonioxSttModel>? models)
    {
        var ids = (models ?? []).Where(model => model.AliasedModelId is null).Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        return ids
            .Select(VersionKey)
            .Where(IsValidVersionKey)
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .Where(key => ids.Contains(RealtimePrefix + key) && ids.Contains(AsyncPrefix + key))
            .OrderByDescending(key => RealtimePrefix + key, ModelIdComparer.Instance)
            .ToArray();
    }

    /// <summary>
    /// Resolves the stored selection to the model id for one mode. A pinned version keeps both modes on the same
    /// generation even when the cached list lacks one of them, because Soniox names both modes the same way.
    /// </summary>
    internal static string ResolveSttModelId(string selection, IReadOnlyList<SonioxSttModel>? models, string mode)
    {
        if (selection == Automatic || !IsValidVersionKey(selection))
            return PreferredSttModelId(models, mode);

        return (mode == AsyncMode ? AsyncPrefix : RealtimePrefix) + selection;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Orders ids by the numbers they contain, so v10 sorts after v9.</summary>
    private sealed class ModelIdComparer : IComparer<string>
    {
        internal static ModelIdComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var left = Numbers(x);
            var right = Numbers(y);
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var compared = left[index].CompareTo(right[index]);
                if (compared != 0)
                    return compared;
            }

            return left.Length != right.Length
                ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(x, y);
        }

        private static long[] Numbers(string? id) =>
            id is null
                ? []
                : VersionNumbers().Matches(id)
                    .Select(match => long.TryParse(match.Value, out var number) ? number : long.MaxValue)
                    .ToArray();
    }
}
