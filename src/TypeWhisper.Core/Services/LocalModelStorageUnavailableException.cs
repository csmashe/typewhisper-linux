using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Represents an unavailable custom local model storage path. Carries a
/// <see cref="Reason" /> and the offending path(s) so the UI can show a localized
/// message; the base <see cref="System.Exception.Message" /> stays English for logs.
/// </summary>
public sealed class LocalModelStorageUnavailableException : IOException
{
    public LocalModelStorageUnavailableException(
        LocalModelStorageUnavailableReason reason,
        string path,
        string message,
        string? currentPath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        Path = path;
        CurrentPath = currentPath;
    }

    /// <summary>Why the path is unavailable; lets the UI layer localize the message.</summary>
    public LocalModelStorageUnavailableReason Reason { get; }

    /// <summary>The offending storage path.</summary>
    public string Path { get; }

    /// <summary>
    /// The current storage path, set only for
    /// <see cref="LocalModelStorageUnavailableReason.NestedUnderCurrentFolder" />.
    /// </summary>
    public string? CurrentPath { get; }
}
