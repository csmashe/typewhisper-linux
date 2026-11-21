using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>
/// A speech-to-text model as <c>GET /v1/model</c> lists it. Every field past the id and name is optional,
/// because the list is not in Reson8's API reference.
/// </summary>
public sealed record Reson8Model(
    string Id,
    string DisplayName,
    IReadOnlyList<string>? Endpoints = null,
    IReadOnlyList<string>? Languages = null,
    bool? IsDefault = null)
{
    internal bool Streams => Endpoints is null || Endpoints.Contains("realtime", StringComparer.OrdinalIgnoreCase);
}

/// <summary>How the transcript treats filler words. Natural sends no <c>filler_mode</c>, Reson8's default.</summary>
internal static class Reson8FillerModes
{
    internal const string Natural = "natural";
    internal const string Clean = "clean";
    internal const string Verbatim = "verbatim";

    private static IReadOnlyList<string> All { get; } = [Natural, Clean, Verbatim];

    internal static string Normalize(string? mode) =>
        All.FirstOrDefault(value => value.Equals(mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Natural;
}

internal static partial class Reson8Models
{
    private const int MaxModels = 100;
    private const int MaxDisplayNameLength = 80;

    // GET /v1/model doesn't say which query parameters a model rejects; this is the table from
    // https://docs.reson8.dev/speech-to-text/models/. Unlisted models take every parameter.
    private static readonly Dictionary<string, HashSet<string>> s_rejectedParameters = new(StringComparer.Ordinal)
    {
        ["rumble-2-flash"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "include_timestamps", "include_words", "phrases", "custom_model_id", "filler_mode",
        },
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguagePattern();

    internal static bool TakesParameter(Reson8Model? model, string parameter) =>
        model is null || !s_rejectedParameters.TryGetValue(model.Id, out var rejected) || !rejected.Contains(parameter);

    /// <summary>The listed models, or null when the answer isn't a model list.</summary>
    internal static List<Reson8Model>? ParseModelListResponse(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var models = Sanitize(doc.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new Reson8Model(
                    GetString(item, "id") ?? "",
                    GetString(item, "displayName") ?? "",
                    StringArray(item, "endpoints"),
                    StringArray(item, "languages"),
                    item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? isDefault.GetBoolean()
                        : null)));
            return models.Count is > 0 and <= MaxModels ? models : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Drops entries an unexpected server answer or a damaged settings file would add.</summary>
    // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- non-nullable only by annotation; deserialization can still produce nulls here.
    internal static List<Reson8Model> Sanitize(IEnumerable<Reson8Model?> models) =>
        models
            .OfType<Reson8Model>()
            .Where(model => model.Id is not null && ModelIdPattern().IsMatch(model.Id)
                && !string.IsNullOrWhiteSpace(model.DisplayName) && !model.DisplayName.Any(char.IsControl))
            .Select(model => model with
            {
                DisplayName = model.DisplayName.Trim() is { Length: > MaxDisplayNameLength } name ? name[..MaxDisplayNameLength] : model.DisplayName.Trim(),
                Endpoints = model.Endpoints?.Where(endpoint => endpoint is not null).Select(endpoint => endpoint.Trim()).ToArray(),
                Languages = model.Languages?.Where(code => code is not null && LanguagePattern().IsMatch(code)).ToArray(),
            })
            .DistinctBy(model => model.Id)
            .ToList();
    // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

    private static string[]? StringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
