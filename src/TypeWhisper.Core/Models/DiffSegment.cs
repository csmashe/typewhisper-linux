namespace TypeWhisper.Core.Models;

/// <summary>How a <see cref="DiffSegment" /> relates the raw text to the final text.</summary>
public enum DiffKind
{
    /// <summary>Present unchanged in both raw and final text.</summary>
    Unchanged,

    /// <summary>Present in the final text but not the raw text.</summary>
    Added,

    /// <summary>Present in the raw text but not the final text.</summary>
    Removed
}

/// <summary>
///     One run of text in an inline raw→final word diff, tagged with whether it
///     was kept, added, or removed. Produced by
///     <see cref="TypeWhisper.Core.Services.WordDiff" /> and rendered by the
///     history Inspect panel.
/// </summary>
public sealed record DiffSegment(string Text, DiffKind Kind);
