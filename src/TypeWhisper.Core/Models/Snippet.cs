namespace TypeWhisper.Core.Models;

/// <summary>
///     A text-expansion rule: when its <see cref="Trigger" /> appears in
///     transcribed text it is replaced with <see cref="Replacement" />.
///     <see cref="TriggerMode" /> controls how the trigger is matched, and
///     <see cref="ProfileIds" /> can scope it to specific profiles.
/// </summary>
public sealed record Snippet
{
    public required string Id { get; init; }
    public required string Trigger { get; init; }
    public required string Replacement { get; init; }
    public bool CaseSensitive { get; init; }
    public bool IsEnabled { get; init; } = true;
    public SnippetTriggerMode TriggerMode { get; init; } = SnippetTriggerMode.Anywhere;
    public int UsageCount { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string Tags { get; init; } = "";
    public IReadOnlyList<string> ProfileIds { get; init; } = [];
}