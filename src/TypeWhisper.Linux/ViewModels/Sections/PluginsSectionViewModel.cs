using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PluginsSectionViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly Dictionary<string, LoadedPlugin> _pluginById = [];

    public ObservableCollection<PluginRow> Plugins { get; } = [];
    public ObservableCollection<PluginFailureRow> LoadFailures { get; } = [];
    public ObservableCollection<PluginSettingFieldRow> SettingFields { get; } = [];
    [ObservableProperty] private string _summary = "";
    public bool HasLoadFailures => LoadFailures.Count > 0;
    [ObservableProperty] private PluginRow? _selectedPlugin;
    [ObservableProperty] private string _settingsTitle = "Plugin settings";
    [ObservableProperty] private string _settingsStatus = "Select a plugin to inspect its configuration options.";
    [ObservableProperty] private bool _canEditSettings;
    [ObservableProperty] private bool _canValidateSettings;

    public PluginsSectionViewModel(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var selectedId = SelectedPlugin?.Id;
        Plugins.Clear();
        _pluginById.Clear();
        foreach (var p in _pluginManager.AllPlugins)
        {
            _pluginById[p.Manifest.Id] = p;
            Plugins.Add(new PluginRow(
                Id: p.Manifest.Id,
                Name: p.Manifest.Name,
                Version: p.Manifest.Version,
                Author: p.Manifest.Author ?? "",
                Description: p.Manifest.Description ?? "",
                IsEnabled: _pluginManager.IsEnabled(p.Manifest.Id)));
        }

        LoadFailures.Clear();
        foreach (var failure in _pluginManager.LoadFailures)
        {
            LoadFailures.Add(new PluginFailureRow(
                Path.GetFileName(failure.PluginDirectory),
                failure.Message));
        }
        OnPropertyChanged(nameof(HasLoadFailures));

        Summary = $"{Plugins.Count} plugin(s) loaded";
        if (LoadFailures.Count > 0)
            Summary += $" · {LoadFailures.Count} failed to load";

        SelectedPlugin = selectedId is not null
            ? Plugins.FirstOrDefault(p => p.Id == selectedId) ?? Plugins.FirstOrDefault()
            : Plugins.FirstOrDefault();
        _ = LoadSelectedPluginSettingsAsync();
    }

    [RelayCommand]
    private async Task ToggleEnabled(PluginRow row)
    {
        if (row.IsEnabled)
            await _pluginManager.DisablePluginAsync(row.Id);
        else
            await _pluginManager.EnablePluginAsync(row.Id);
    }

    partial void OnSelectedPluginChanged(PluginRow? value)
    {
        _ = LoadSelectedPluginSettingsAsync();
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (SelectedPlugin is null || !_pluginById.TryGetValue(SelectedPlugin.Id, out var loaded))
            return;

        if (loaded.Instance is not IPluginSettingsProvider provider)
            return;

        foreach (var field in SettingFields)
            await provider.SetSettingValueAsync(field.Key, field.Value);

        SettingsStatus = "Settings saved.";
    }

    [RelayCommand]
    private async Task ValidateSettingsAsync()
    {
        if (SelectedPlugin is null || !_pluginById.TryGetValue(SelectedPlugin.Id, out var loaded))
            return;

        if (loaded.Instance is not IPluginSettingsProvider provider)
            return;

        foreach (var field in SettingFields)
            await provider.SetSettingValueAsync(field.Key, field.Value);

        var result = await provider.ValidateAsync();
        SettingsStatus = result?.Message ?? "No validation available.";
        await LoadSelectedPluginSettingsAsync();
    }

    private async Task LoadSelectedPluginSettingsAsync()
    {
        SettingFields.Clear();
        CanEditSettings = false;
        CanValidateSettings = false;

        if (SelectedPlugin is null || !_pluginById.TryGetValue(SelectedPlugin.Id, out var loaded))
        {
            SettingsTitle = "Plugin settings";
            SettingsStatus = "Select a plugin to inspect its configuration options.";
            return;
        }

        SettingsTitle = $"{SelectedPlugin.Name} settings";

        if (loaded.Instance is not IPluginSettingsProvider provider)
        {
            SettingsStatus = "This plugin does not expose host-neutral settings yet.";
            return;
        }

        foreach (var definition in provider.GetSettingDefinitions())
        {
            var value = await provider.GetSettingValueAsync(definition.Key) ?? string.Empty;
            SettingFields.Add(new PluginSettingFieldRow(
                definition.Key,
                definition.Label,
                definition.Description ?? string.Empty,
                definition.Placeholder ?? string.Empty,
                definition.Options ?? [],
                definition.IsSecret,
                value));
        }

        CanEditSettings = SettingFields.Count > 0;
        CanValidateSettings = true;
        SettingsStatus = SettingFields.Count > 0
            ? "Edit the values below and click Save."
            : "This plugin exposes a settings provider, but no editable fields.";
    }
}

public sealed record PluginRow(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    bool IsEnabled);

public sealed record PluginFailureRow(
    string FolderName,
    string Message);

public sealed partial class PluginSettingFieldRow : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string Description { get; }
    public string Placeholder { get; }
    public IReadOnlyList<PluginSettingOption> Options { get; }
    public bool HasOptions => Options.Count > 0;
    public bool IsSecret { get; }

    [ObservableProperty] private string _value;
    [ObservableProperty] private PluginSettingOption? _selectedOption;

    public PluginSettingFieldRow(
        string key,
        string label,
        string description,
        string placeholder,
        IReadOnlyList<PluginSettingOption> options,
        bool isSecret,
        string value)
    {
        Key = key;
        Label = label;
        Description = description;
        Placeholder = placeholder;
        Options = options;
        IsSecret = isSecret;
        _value = value;
        _selectedOption = Options.FirstOrDefault(o => o.Value == value) ?? Options.FirstOrDefault();
        if (_selectedOption is not null && string.IsNullOrEmpty(_value))
            _value = _selectedOption.Value;
    }

    partial void OnSelectedOptionChanged(PluginSettingOption? value)
    {
        if (value is not null && _value != value.Value)
            Value = value.Value;
    }

    partial void OnValueChanged(string value)
    {
        if (!HasOptions)
            return;

        var option = Options.FirstOrDefault(o => o.Value == value);
        if (!Equals(_selectedOption, option))
            SelectedOption = option;
    }
}
