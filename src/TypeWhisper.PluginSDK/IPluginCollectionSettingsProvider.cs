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
    IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions();

    /// <summary>Returns the current items for the given collection key.</summary>
    Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
        // ReSharper disable UnusedParameter.Global
        string collectionKey,
        CancellationToken ct = default
        // ReSharper restore UnusedParameter.Global
    );

    /// <summary>
    ///     Replaces the items for the given collection key. Returns a
    ///     <see cref="PluginSettingsValidationResult" /> — check <c>IsSuccess</c>;
    ///     on failure <c>Message</c> explains why the items were rejected.
    /// </summary>
    Task<PluginSettingsValidationResult> SetItemsAsync(
        // ReSharper disable UnusedParameter.Global
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default
        // ReSharper restore UnusedParameter.Global
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
)
{
    /// <summary>
    ///     Shows a search box above the items that hides rows whose visible text does not
    ///     contain the query. Filtering only affects the view; saving still sends every item.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public bool IsSearchable { get; init; }

    /// <summary>
    ///     Starting points the user can add as new unsaved items. Adding one copies its values
    ///     into the draft; nothing is stored until the user saves. Empty hides the template picker.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public IReadOnlyList<PluginCollectionTemplate> Templates { get; init; } = [];

    /// <summary>
    ///     Offers a test of each item's unsaved draft when the plugin implements
    ///     <see cref="IPluginCollectionItemTester" />. Null hides the test controls.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public PluginCollectionItemTest? ItemTest { get; init; }
}

/// <summary>A ready-made item the user can add to a collection.</summary>
/// <param name="Id">Unique template identifier within the collection.</param>
/// <param name="Label">Name shown in the template picker.</param>
/// <param name="Description">Optional explanation shown with the picker.</param>
/// <param name="Values">
///     Field values for the new item. Fields not listed get the same defaults as an empty item;
///     internal "__" fields such as "__id" are always generated fresh.
/// </param>
// ReSharper disable once UnusedType.Global
public sealed record PluginCollectionTemplate(
    string Id,
    string Label,
    string? Description,
    IReadOnlyDictionary<string, string?> Values
)
{
    // A collapsed ComboBox announces its selected item through ToString().
    public override string ToString() => Label;
}

/// <summary>Describes the per-item test the host offers for a collection.</summary>
/// <param name="ButtonLabel">Text of the button that runs the test.</param>
/// <param name="SampleInput">Initial, editable test input.</param>
// ReSharper disable once UnusedType.Global
public sealed record PluginCollectionItemTest(string ButtonLabel, string SampleInput);

/// <summary>A single item in a plugin collection, represented as a field-value map.</summary>
// ReSharper disable once UnusedType.Global
public sealed record PluginCollectionItem(IReadOnlyDictionary<string, string?> Values);
