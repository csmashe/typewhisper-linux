// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Describes a model available from a plugin provider.
/// </summary>
/// <param name="Id">Model identifier (e.g. "gpt-4o", "whisper-1").</param>
/// <param name="DisplayName">Human-readable name for the UI.</param>
// ReSharper disable once UnusedType.Global
public sealed record PluginModelInfo(string Id, string DisplayName)
{
    /// <summary>Human-readable size description (e.g. "~670 MB").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? SizeDescription { get; init; }

    // Public SDK API: renaming to the rule's suggested "Mb" would break binary compat for
    // external plugins compiled against this name, so the pascal-case suggestion is not
    // applied here. 
    /// <summary>Estimated download size in megabytes.</summary>
    // ReSharper disable once InconsistentNaming
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public long EstimatedSizeMB { get; init; }

    /// <summary>Whether this model is recommended for new users.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public bool IsRecommended { get; init; }

    /// <summary>Number of languages supported by this model.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public int LanguageCount { get; init; }
}
