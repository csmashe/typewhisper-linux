// ReSharper disable ArrangeObjectCreationWhenTypeNotEvident -- target-typed `new(...)` inside collection
// expressions and record construction is the prevailing style across this codebase.
using TypeWhisper.PluginSDK;

namespace TypeWhisper.UiTests;

/// <summary>
///     A plugin with one setting of each kind and a templated, testable collection, so the render
///     checks reach the generic plugin settings editor.
/// </summary>
internal sealed class UiSettingsPlugin
    : ITypeWhisperPlugin, IPluginSettingsProvider, IPluginCollectionSettingsProvider, IPluginCollectionItemTester
{
    internal const string Id = "com.typewhisper.ui-settings";

    private const string CollectionKey = "scripts";

    private static readonly IReadOnlyList<PluginSettingOption> s_shells =
        [new("bash", "bash"), new("sh", "sh")];

    private readonly Dictionary<string, string?> _values = new() { ["mode"] = "fast", ["enabled"] = "true" };

    private IReadOnlyList<PluginCollectionItem> _items =
    [
        new(new Dictionary<string, string?>
        {
            ["name"] = "Uppercase", ["command"] = "tr a-z A-Z", ["shell"] = "bash", ["enabled"] = "true",
        }),
    ];

    public string PluginId => Id;
    public string PluginName => "Settings Sample";
    public string PluginVersion => "1.0.0";

    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
    public Task DeactivateAsync() => Task.CompletedTask;
    public void Dispose() { }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new("endpoint", "Endpoint", Kind: PluginSettingKind.Text),
        new("mode", "Mode", Options: [new("fast", "Fast"), new("accurate", "Accurate")], Kind: PluginSettingKind.Dropdown),
        new("token", "Token", IsSecret: true),
        new("notes", "Notes", Kind: PluginSettingKind.Multiline),
        new("enabled", "Use sample service", Kind: PluginSettingKind.Boolean),
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
    [
        new(CollectionKey, "Scripts", null,
            [
                new("name", "Name", Kind: PluginSettingKind.Text),
                new("command", "Command", Kind: PluginSettingKind.Multiline),
                new("shell", "Shell", Options: s_shells, Kind: PluginSettingKind.Dropdown),
                new("enabled", "Enabled", Kind: PluginSettingKind.Boolean),
            ],
            ItemLabelFieldKey: "name")
        {
            IsSearchable = true,
            Templates = [new("lower", "Lowercase", "Converts the text to lowercase.",
                new Dictionary<string, string?> { ["name"] = "Lowercase", ["command"] = "tr A-Z a-z", ["shell"] = "bash" })],
            ItemTest = new PluginCollectionItemTest("Test", "Sample input"),
        },
    ];

    public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(string collectionKey, CancellationToken ct = default) =>
        Task.FromResult(_items);

    public Task<PluginSettingsValidationResult> SetItemsAsync(
        string collectionKey,
        IReadOnlyList<PluginCollectionItem> items,
        CancellationToken ct = default)
    {
        _items = items;
        return Task.FromResult(new PluginSettingsValidationResult(true, ""));
    }

    public Task<PluginCollectionItemTestResult> TestItemAsync(
        string collectionKey,
        PluginCollectionItem draft,
        string input,
        CancellationToken ct) =>
        Task.FromResult(new PluginCollectionItemTestResult(true, "", input.ToUpperInvariant()));
}
