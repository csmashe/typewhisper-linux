namespace TypeWhisper.Core.Models;

/// <summary>
///     Localized labels for history export methods.
///     Callers pass localized strings; defaults are English.
/// </summary>
public sealed record ExportLabels
{
    public string Header { get; init; } = "TypeWhisper — Transcription History";
    public string Exported { get; init; } = "Exported";
    public string Entries { get; init; } = "Entries";
    public string Timestamp { get; init; } = "Timestamp";
    public string App { get; init; } = "App";
    public string Text { get; init; } = "Text";
    public string Duration { get; init; } = "Duration (s)";
    public string Words { get; init; } = "Words";
    public string Language { get; init; } = "Language";

    public static ExportLabels Default { get; } = new();
}