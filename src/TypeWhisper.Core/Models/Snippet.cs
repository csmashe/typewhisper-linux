namespace TypeWhisper.Core.Models;

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

public enum SnippetTriggerMode
{
    Anywhere,
    ExactPhrase
}
