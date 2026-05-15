namespace TypeWhisper.PluginSDK;

public interface IPluginCollectionSettingsProvider
{
    IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions();
    Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(string collectionKey, CancellationToken ct = default);
    Task<PluginSettingsValidationResult> SetItemsAsync(string collectionKey,
        IReadOnlyList<PluginCollectionItem> items, CancellationToken ct = default);
}

public sealed record PluginCollectionDefinition(
    string Key,
    string Label,
    string? Description,
    IReadOnlyList<PluginSettingDefinition> ItemFields,
    string? ItemLabelFieldKey = null,
    string? AddButtonLabel = null);

public sealed record PluginCollectionItem(IReadOnlyDictionary<string, string?> Values);
