namespace TypeWhisper.Core.Services;

/// <summary>
/// Why a custom local model storage path could not be used. The Core layer has no
/// access to the UI localization catalog, so it surfaces a structured reason and the
/// offending path(s) and lets the UI layer render a localized message.
/// </summary>
public enum LocalModelStorageUnavailableReason
{
    /// <summary>The configured custom storage folder does not exist.</summary>
    DoesNotExist,

    /// <summary>The folder exists but cannot be written to.</summary>
    NotWritable,

    /// <summary>The chosen target folder is nested inside the current storage folder.</summary>
    NestedUnderCurrentFolder
}

/// <summary>
/// Represents an unavailable custom local model storage path. Carries a
/// <see cref="Reason" /> and the offending path(s) so the UI can show a localized
/// message; the base <see cref="System.Exception.Message" /> stays English for logs.
/// </summary>
public sealed class LocalModelStorageUnavailableException : IOException
{
    /// <summary>
    /// Initializes a new instance of the LocalModelStorageUnavailableException class.
    /// </summary>
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
