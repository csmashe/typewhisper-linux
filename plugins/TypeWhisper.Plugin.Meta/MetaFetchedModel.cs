using System.Text.Json.Serialization;

namespace TypeWhisper.Plugin.Meta;

internal sealed record MetaFetchedModel(
    [property: JsonPropertyName("id")] string Id,
    // ReSharper disable once NotAccessedPositionalProperty.Global -- bound from the models endpoint and round-tripped through the persisted settings JSON.
    [property: JsonPropertyName("owned_by")] string? OwnedBy);

internal sealed record MetaModelCatalog(
    IReadOnlyList<MetaFetchedModel> LlmModels,
    IReadOnlyList<MetaFetchedModel> TranscriptionModels);
