using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PluginCollectionRow : ObservableObject
{
    public PluginCollectionRow(
        PluginCollectionDefinition definition,
        PluginRow ownerRow,
        IReadOnlyList<PluginCollectionItem> items
    )
    {
        Key = definition.Key;
        Label = definition.Label;
        Description = definition.Description;
        AddButtonLabel = definition.AddButtonLabel ?? "Add item";
        ItemFields = definition.ItemFields;
        ItemLabelFieldKey = definition.ItemLabelFieldKey;
        OwnerRow = ownerRow;

        foreach (var item in items)
        {
            var row = new PluginCollectionItemRow(ItemFields, item, ItemLabelFieldKey) { OwnerCollection = this };
            Items.Add(row);
        }
    }

    public string Key { get; }
    public string Label { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string AddButtonLabel { get; }
    private IReadOnlyList<PluginSettingDefinition> ItemFields { get; }
    private string? ItemLabelFieldKey { get; }
    public ObservableCollection<PluginCollectionItemRow> Items { get; } = [];
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  parent back-reference retained in the row's object graph; not safely removable without changing the constructor contract
    public PluginRow OwnerRow { get; }

    [RelayCommand]
    private void AddItem()
    {
        // Seed new items with sensible defaults so the UI shows valid controls
        // before the user touches anything. Fields whose key starts with "__"
        // are internal (e.g. "__id") and always get a fresh GUID.
        var seed = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in ItemFields)
        {
            if (field.Key.StartsWith("__", StringComparison.Ordinal))
            {
                seed[field.Key] = Guid.NewGuid().ToString("D");
                continue;
            }

            var kind = field.Kind;
            if (kind == PluginSettingKind.Auto && field.Options is { Count: > 0 })
            {
                kind = PluginSettingKind.Dropdown;
            }

            seed[field.Key] = kind switch
            {
                PluginSettingKind.Boolean => "true",
                PluginSettingKind.Dropdown when field.Options is { Count: > 0 } => field.Options[0].Value,
                _ => string.Empty
            };
        }

        var item = new PluginCollectionItemRow(
            ItemFields,
            new PluginCollectionItem(seed),
            ItemLabelFieldKey
        ) { OwnerCollection = this };
        Items.Add(item);
    }

    [RelayCommand]
    private void RemoveItem(PluginCollectionItemRow item)
    {
        Items.Remove(item);
    }

    [RelayCommand]
    private void MoveUp(PluginCollectionItemRow item)
    {
        var index = Items.IndexOf(item);
        if (index > 0)
        {
            Items.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDown(PluginCollectionItemRow item)
    {
        var index = Items.IndexOf(item);
        if (index >= 0 && index < Items.Count - 1)
        {
            Items.Move(index, index + 1);
        }
    }
}

public class PluginCollectionItemRow : ObservableObject
{
    private readonly PluginSettingFieldRow? _labelField;

    public PluginCollectionItemRow(
        IReadOnlyList<PluginSettingDefinition> itemFields,
        PluginCollectionItem? source,
        string? itemLabelFieldKey
    )
    {
        var hasId = false;
        foreach (var definition in itemFields)
        {
            var value = string.Empty;
            if (source is not null && source.Values.TryGetValue(definition.Key, out var raw))
            {
                value = raw ?? string.Empty;
            }

            var field = new PluginSettingFieldRow(
                definition.Key,
                definition.Label,
                definition.Description ?? string.Empty,
                definition.Placeholder ?? string.Empty,
                definition.Options ?? [],
                definition.IsSecret,
                definition.Kind,
                value
            );
            Fields.Add(field);

            if (definition.Key == "__id")
            {
                hasId = true;
            }
        }

        if (!hasId)
        {
            var idValue = string.Empty;
            if (source is not null && source.Values.TryGetValue("__id", out var rawId))
            {
                idValue = rawId ?? string.Empty;
            }

            if (string.IsNullOrEmpty(idValue))
            {
                idValue = Guid.NewGuid().ToString("D");
            }

            Fields.Add(
                new PluginSettingFieldRow(
                    "__id",
                    "Id",
                    string.Empty,
                    string.Empty,
                    [],
                    false,
                    PluginSettingKind.Text,
                    idValue
                )
            );
        }
        else
        {
            // The plugin declares an "__id" field, but the source item may
            // carry it empty (or omit it). Generate one so HiddenId is always
            // populated and item identity stays stable across saves.
            var idField = Fields.First(f => f.Key == "__id");
            if (string.IsNullOrEmpty(idField.Value))
            {
                idField.Value = Guid.NewGuid().ToString("D");
            }
        }

        if (string.IsNullOrEmpty(itemLabelFieldKey))
        {
            return;
        }

        {
            _labelField = Fields.FirstOrDefault(f => f.Key == itemLabelFieldKey);
            if (_labelField is not null)
            {
                _labelField.PropertyChanged += OnLabelFieldChanged;
            }
        }
    }

    public ObservableCollection<PluginSettingFieldRow> Fields { get; } = [];

    public PluginCollectionRow? OwnerCollection { get; init; }

    public string HiddenId
    {
        get => Fields.FirstOrDefault(f => f.Key == "__id")?.Value ?? string.Empty;
        // ReSharper disable once UnusedMember.Global  paired write accessor for the collection-item identity property (mirrors the __id field); not currently called in-tree
        set
        {
            var idField = Fields.FirstOrDefault(f => f.Key == "__id");
            idField?.Value = value;
        }
    }

    public string HeaderText
    {
        get
        {
            var value = _labelField?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return _labelField is null ? "(new item)" : "(unnamed)";
            }

            return value;
        }
    }

    private void OnLabelFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginSettingFieldRow.Value))
        {
            OnPropertyChanged(nameof(HeaderText));
        }
    }
}