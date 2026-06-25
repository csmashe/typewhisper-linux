namespace TypeWhisper.Core.Models;

/// <summary>Canonical <see cref="ErrorLogEntry.Category" /> string constants so producers and filters agree on the same set.</summary>
public static class ErrorCategory
{
    public const string General = "general";
    public const string Transcription = "transcription";
    public const string Recording = "recording";
    public const string Prompt = "prompt";
    public const string Plugin = "plugin";
    public const string Insertion = "insertion";

    /// <summary>Active-window / URL detection failures (e.g. compositor query, AT-SPI walk).</summary>
    public const string Detection = "detection";
}
