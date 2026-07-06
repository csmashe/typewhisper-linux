namespace TypeWhisper.Core.Models;

/// <summary>
///     Why a custom local model storage path could not be used. The Core layer has no
///     access to the UI localization catalog, so it surfaces a structured reason and the
///     offending path(s) and lets the UI layer render a localized message.
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
