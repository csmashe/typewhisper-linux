// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Text.Json.Serialization;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed record OpenAiFetchedModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("owned_by")] string? OwnedBy);

internal sealed record OpenAiChatGptModel(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("priority")] int? Priority,
    [property: JsonPropertyName("available_in_plans")] List<string>? AvailableInPlans);
