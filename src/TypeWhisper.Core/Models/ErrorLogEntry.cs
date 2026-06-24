namespace TypeWhisper.Core.Models;

/// <summary>
///     A single recorded error surfaced to the user-facing error log, tagged with
///     a <see cref="Category" /> from <see cref="ErrorCategory" />. Use
///     <see cref="Create" /> to stamp a fresh id and UTC timestamp.
/// </summary>
public sealed record ErrorLogEntry
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Message { get; init; }
    public string Category { get; init; } = "general";

    public static ErrorLogEntry Create(string message, string category = "general")
    {
        return new ErrorLogEntry
        {
            Id = Guid.NewGuid().ToString("N"), Timestamp = DateTime.UtcNow, Message = message, Category = category
        };
    }
}