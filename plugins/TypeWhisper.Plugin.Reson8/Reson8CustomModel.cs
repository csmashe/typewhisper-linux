// ReSharper disable NotAccessedPositionalProperty.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

namespace TypeWhisper.Plugin.Reson8;

public sealed record Reson8CustomModel(
    string Id,
    string Name,
    string? Description,
    int? PhraseCount);
