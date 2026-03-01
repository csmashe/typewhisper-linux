namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes a model available from a plugin provider.
/// </summary>
/// <param name="Id">Model identifier (e.g. "gpt-4o", "whisper-1").</param>
/// <param name="DisplayName">Human-readable name for the UI.</param>
public sealed record PluginModelInfo(string Id, string DisplayName)
{
    /// <summary>Human-readable size description (e.g. "~670 MB").</summary>
    public string? SizeDescription { get; init; }

    /// <summary>Estimated download size in megabytes.</summary>
    public long EstimatedSizeMB { get; init; }

    /// <summary>Whether this model is recommended for new users.</summary>
    public bool IsRecommended { get; init; }

    /// <summary>Number of languages supported by this model.</summary>
    public int LanguageCount { get; init; }
}
