// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional interface for plugins that expose ordered, user-editable lists of items
///     (e.g. a list of custom prompts or word replacements). The host renders a generic
///     collection editor UI driven by the definitions returned here.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IPluginCollectionSettingsProvider
{
    /// <summary>Returns metadata for each collection the plugin exposes.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions();

    /// <summary>Returns the current items for the given collection key.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
        string collectionKey,
        CancellationToken ct = default
    );

    /// <summary>
    ///     Replaces the items for the given collection key. Returns a
    ///     <see cref="PluginSettingsValidationResult" /> — check <c>IsSuccess</c>;
    ///     on failure <c>Message</c> explains why the items were rejected.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default
    );
}

/// <summary>
///     Describes a single collection exposed by a plugin.
/// </summary>
/// <param name="Key">Unique key identifying this collection within the plugin.</param>
/// <param name="Label">Display label shown in the settings UI.</param>
/// <param name="Description">Optional description shown below the label.</param>
/// <param name="ItemFields">Field definitions for each item row in the collection.</param>
/// <param name="ItemLabelFieldKey">Key of the field used as the row label in the UI, or null.</param>
/// <param name="AddButtonLabel">Custom label for the add-item button, or null for the default.</param>
// ReSharper disable once UnusedType.Global
public sealed record PluginCollectionDefinition(
    string Key,
    string Label,
    string? Description,
    IReadOnlyList<PluginSettingDefinition> ItemFields,
    string? ItemLabelFieldKey = null,
    string? AddButtonLabel = null
);

/// <summary>A single item in a plugin collection, represented as a field-value map.</summary>
// ReSharper disable once UnusedType.Global
public sealed record PluginCollectionItem(IReadOnlyDictionary<string, string?> Values);
