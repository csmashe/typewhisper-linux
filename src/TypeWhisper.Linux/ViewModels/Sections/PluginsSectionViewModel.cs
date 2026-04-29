using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PluginsSectionViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly Dictionary<string, LoadedPlugin> _pluginById = [];

    public ObservableCollection<PluginCategoryGroup> PluginGroups { get; } = [];
    public ObservableCollection<PluginFailureRow> LoadFailures { get; } = [];
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _headerSummary = "";
    public bool HasLoadFailures => LoadFailures.Count > 0;

    public PluginsSectionViewModel(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var expandedPluginId = PluginGroups
            .SelectMany(group => group.Plugins)
            .FirstOrDefault(plugin => plugin.IsExpanded)?
            .Id;

        PluginGroups.Clear();
        _pluginById.Clear();

        var plugins = _pluginManager.AllPlugins
            .Select(p =>
            {
                _pluginById[p.Manifest.Id] = p;
                return new PluginRow(
                    owner: this,
                    id: p.Manifest.Id,
                    name: p.Manifest.Name,
                    version: p.Manifest.Version,
                    author: p.Manifest.Author ?? "",
                    description: p.Manifest.Description ?? "",
                    category: InferCategory(p.Manifest),
                    isLocal: InferIsLocal(p.Manifest),
                    hasExpandableSettings: p.Instance is IPluginSettingsProvider provider &&
                                           provider.GetSettingDefinitions().Count > 0,
                    isEnabled: _pluginManager.IsEnabled(p.Manifest.Id));
            })
            .OrderBy(p => p.CategorySortOrder)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in plugins.GroupBy(p => p.CategoryKey))
        {
            var categoryPlugins = group.ToList();
            var categoryLabel = categoryPlugins[0].CategoryLabel;
            PluginGroups.Add(new PluginCategoryGroup(categoryLabel, categoryPlugins));
        }

        LoadFailures.Clear();
        foreach (var failure in _pluginManager.LoadFailures)
        {
            LoadFailures.Add(new PluginFailureRow(
                Path.GetFileName(failure.PluginDirectory),
                failure.Message));
        }
        OnPropertyChanged(nameof(HasLoadFailures));

        Summary = $"{plugins.Count} plugin(s) loaded";
        if (LoadFailures.Count > 0)
            Summary += $" · {LoadFailures.Count} failed to load";

        var enabledCount = plugins.Count(p => p.IsEnabled);
        HeaderSummary = $"{plugins.Count} installed, {enabledCount} enabled";

        if (expandedPluginId is null)
            return;

        var expandedPlugin = PluginGroups
            .SelectMany(group => group.Plugins)
            .FirstOrDefault(plugin => plugin.Id == expandedPluginId);
        if (expandedPlugin is not null)
        {
            expandedPlugin.IsExpanded = true;
            _ = LoadPluginSettingsAsync(expandedPlugin);
        }
    }

    [RelayCommand]
    private async Task ToggleEnabled(PluginRow row)
    {
        if (row.IsEnabled)
            await _pluginManager.DisablePluginAsync(row.Id);
        else
            await _pluginManager.EnablePluginAsync(row.Id);
    }

    [RelayCommand]
    private async Task ToggleExpandedAsync(PluginRow row)
    {
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            return;
        }

        foreach (var other in PluginGroups.SelectMany(group => group.Plugins).Where(p => p != row))
            other.IsExpanded = false;

        row.IsExpanded = true;
        await LoadPluginSettingsAsync(row);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync(PluginRow row)
    {
        if (!_pluginById.TryGetValue(row.Id, out var loaded))
            return;

        if (loaded.Instance is not IPluginSettingsProvider provider)
            return;

        foreach (var field in row.SettingFields)
            await provider.SetSettingValueAsync(field.Key, field.Value);

        row.Status = "Settings saved.";
    }

    [RelayCommand]
    private async Task ValidateSettingsAsync(PluginRow row)
    {
        if (!_pluginById.TryGetValue(row.Id, out var loaded))
            return;

        if (loaded.Instance is not IPluginSettingsProvider provider)
            return;

        foreach (var field in row.SettingFields)
            await provider.SetSettingValueAsync(field.Key, field.Value);

        var result = await provider.ValidateAsync();
        row.Status = result?.Message ?? "No validation available.";
        await LoadPluginSettingsAsync(row, preserveStatus: true);
    }

    private async Task LoadPluginSettingsAsync(PluginRow row, bool preserveStatus = false)
    {
        row.SettingFields.Clear();
        row.CanEditSettings = false;
        row.CanValidateSettings = false;

        if (!_pluginById.TryGetValue(row.Id, out var loaded))
        {
            row.Status = "Unable to load plugin settings.";
            return;
        }

        if (loaded.Instance is not IPluginSettingsProvider provider)
        {
            row.Status = "This plugin does not expose host-neutral settings yet.";
            return;
        }

        foreach (var definition in provider.GetSettingDefinitions())
        {
            var value = await provider.GetSettingValueAsync(definition.Key) ?? string.Empty;
            row.SettingFields.Add(new PluginSettingFieldRow(
                definition.Key,
                definition.Label,
                definition.Description ?? string.Empty,
                definition.Placeholder ?? string.Empty,
                definition.Options ?? [],
                definition.IsSecret,
                value));
        }

        row.CanEditSettings = row.SettingFields.Count > 0;
        row.CanValidateSettings = true;
        if (!preserveStatus)
        {
            row.Status = row.SettingFields.Count > 0
                ? "Edit the values below and click Save."
                : "This plugin exposes a settings provider, but no editable fields.";
        }
    }

    private static bool InferIsLocal(PluginManifest manifest)
    {
        if (manifest.IsLocal)
            return true;

        var id = manifest.Id.Trim().ToLowerInvariant();
        if (KnownLocalPluginIds.Contains(id))
            return true;
        if (KnownCloudPluginIds.Contains(id))
            return false;

        var combined = $"{manifest.Name} {manifest.Description}".ToLowerInvariant();
        if (combined.Contains("offline") ||
            combined.Contains("local") ||
            combined.Contains("on-device") ||
            combined.Contains("on device") ||
            combined.Contains("file-based") ||
            combined.Contains("file based") ||
            combined.Contains("obsidian") ||
            combined.Contains("shell script") ||
            combined.Contains("webhook"))
        {
            return true;
        }

        return false;
    }

    private static string? InferCategory(PluginManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Category))
            return manifest.Category;

        var id = manifest.Id.Trim().ToLowerInvariant();
        if (TranscriptionPluginIds.Contains(id))
            return "transcription";
        if (LlmPluginIds.Contains(id))
            return "llm";
        if (ActionPluginIds.Contains(id))
            return "action";
        if (MemoryPluginIds.Contains(id))
            return "memory";
        if (UtilityPluginIds.Contains(id))
            return "utility";

        var combined = $"{manifest.Name} {manifest.Description}".ToLowerInvariant();
        if (combined.Contains("transcription") ||
            combined.Contains("speech-to-text") ||
            combined.Contains("speech to text") ||
            combined.Contains("asr"))
        {
            return "transcription";
        }

        if (combined.Contains("llm") ||
            combined.Contains("prompt") ||
            combined.Contains("inference") ||
            combined.Contains("multi-model"))
        {
            return "llm";
        }

        if (combined.Contains("memory"))
            return "memory";

        if (combined.Contains("issue") ||
            combined.Contains("obsidian") ||
            combined.Contains("webhook") ||
            combined.Contains("script"))
        {
            return "action";
        }

        return "utility";
    }

    private static readonly HashSet<string> KnownLocalPluginIds =
    [
        "com.typewhisper.whisper-cpp",
        "com.typewhisper.sherpa-onnx",
        "com.typewhisper.file-memory",
        "com.typewhisper.obsidian",
        "com.typewhisper.script",
        "com.typewhisper.webhook"
    ];

    private static readonly HashSet<string> KnownCloudPluginIds =
    [
        "com.typewhisper.assemblyai",
        "com.typewhisper.cerebras",
        "com.typewhisper.claude",
        "com.typewhisper.cloudflare-asr",
        "com.typewhisper.cohere",
        "com.typewhisper.deepgram",
        "com.typewhisper.fireworks",
        "com.typewhisper.gemini",
        "com.typewhisper.gladia",
        "com.typewhisper.google-cloud-stt",
        "com.typewhisper.groq",
        "com.typewhisper.linear",
        "com.typewhisper.openai",
        "com.typewhisper.openai-compatible",
        "com.typewhisper.openrouter",
        "com.typewhisper.qwen3-stt",
        "com.typewhisper.soniox",
        "com.typewhisper.speechmatics",
        "com.typewhisper.voxtral"
    ];

    private static readonly HashSet<string> TranscriptionPluginIds =
    [
        "com.typewhisper.assemblyai",
        "com.typewhisper.cloudflare-asr",
        "com.typewhisper.deepgram",
        "com.typewhisper.gladia",
        "com.typewhisper.google-cloud-stt",
        "com.typewhisper.granite-speech",
        "com.typewhisper.openai",
        "com.typewhisper.qwen3-stt",
        "com.typewhisper.sherpa-onnx",
        "com.typewhisper.soniox",
        "com.typewhisper.speechmatics",
        "com.typewhisper.voxtral",
        "com.typewhisper.whisper-cpp"
    ];

    private static readonly HashSet<string> LlmPluginIds =
    [
        "com.typewhisper.cerebras",
        "com.typewhisper.claude",
        "com.typewhisper.cohere",
        "com.typewhisper.fireworks",
        "com.typewhisper.gemini",
        "com.typewhisper.gemma-local",
        "com.typewhisper.groq",
        "com.typewhisper.openai-compatible",
        "com.typewhisper.openrouter"
    ];

    private static readonly HashSet<string> ActionPluginIds =
    [
        "com.typewhisper.linear",
        "com.typewhisper.obsidian",
        "com.typewhisper.script",
        "com.typewhisper.webhook",
        "com.typewhisper.live-transcript"
    ];

    private static readonly HashSet<string> MemoryPluginIds =
    [
        "com.typewhisper.file-memory",
        "com.typewhisper.openai-vector-memory"
    ];

    private static readonly HashSet<string> UtilityPluginIds =
    [
        "com.typewhisper.openai-compatible"
    ];
}

public sealed class PluginCategoryGroup
{
    public string Title { get; }
    public ObservableCollection<PluginRow> Plugins { get; }

    public PluginCategoryGroup(string title, IEnumerable<PluginRow> plugins)
    {
        Title = title;
        Plugins = new ObservableCollection<PluginRow>(plugins);
    }
}

public partial class PluginRow : ObservableObject
{
    private readonly PluginsSectionViewModel? _owner;

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Author { get; }
    public string Description { get; }
    public string CategoryKey { get; }
    public string CategoryLabel { get; }
    public int CategorySortOrder { get; }
    public bool IsLocal { get; }
    public string LocationBadge => IsLocal ? "Local" : "Cloud";
    public string StatusBadge => IsEnabled ? "Enabled" : "Disabled";
    public string LocationBadgeBackground => IsLocal ? "#1B2F24" : "#1A3453";
    public string LocationBadgeBorder => IsLocal ? "#2F5E45" : "#2E5B89";
    public string LocationBadgeForeground => IsLocal ? "#D8F3E5" : "#D6E7FF";
    public string StatusBadgeBackground => IsEnabled ? "#173222" : "#3A1F1F";
    public string StatusBadgeBorder => IsEnabled ? "#2F7D4E" : "#8A3A3A";
    public string StatusBadgeForeground => IsEnabled ? "#D9FBE7" : "#FFD9D9";
    public string Monogram => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part => char.ToUpperInvariant(part[0])));
    public string ExpansionGlyph => IsExpanded ? "⌃" : "⌄";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasExpandableSettings { get; }
    public ObservableCollection<PluginSettingFieldRow> SettingFields { get; } = [];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _status = "Expand to edit plugin settings.";
    [ObservableProperty] private bool _canEditSettings;
    [ObservableProperty] private bool _canValidateSettings;

    public PluginRow(
        PluginsSectionViewModel? owner,
        string id,
        string name,
        string version,
        string author,
        string description,
        string? category,
        bool isLocal,
        bool hasExpandableSettings,
        bool isEnabled)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Version = version;
        Author = author;
        Description = description;
        IsLocal = isLocal;
        HasExpandableSettings = hasExpandableSettings;
        IsEnabled = isEnabled;

        var descriptor = PluginCategories.Resolve(category);
        CategoryKey = descriptor.Key;
        CategoryLabel = descriptor.DisplayName;
        CategorySortOrder = descriptor.SortOrder;
    }

    public PluginsSectionViewModel? Owner => _owner;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpansionGlyph));
    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(StatusBadgeBackground));
        OnPropertyChanged(nameof(StatusBadgeBorder));
        OnPropertyChanged(nameof(StatusBadgeForeground));
    }
}

public sealed record PluginFailureRow(
    string FolderName,
    string Message);

public sealed partial class PluginSettingFieldRow : ObservableObject
{
    public string Key { get; }
    public string Label { get; }
    public string Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
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

internal sealed record PluginCategoryInfo(string Key, string DisplayName, int SortOrder);

internal static class PluginCategories
{
    public static PluginCategoryInfo Resolve(string? rawCategory) => Normalize(rawCategory) switch
    {
        "transcription" => new("transcription", "Transcription Engines", 0),
        "llm" => new("llm", "LLM Providers", 1),
        "post-processing" => new("post-processing", "Post-Processors", 2),
        "action" => new("action", "Actions", 3),
        "memory" => new("memory", "Memory", 4),
        _ => new("utility", "Utilities", 5)
    };

    private static string Normalize(string? rawCategory) => rawCategory?.Trim().ToLowerInvariant() switch
    {
        "transcription" => "transcription",
        "llm" => "llm",
        "postprocessing" or "post-processing" or "postprocessor" or "post-processor" => "post-processing",
        "action" => "action",
        "memory" => "memory",
        _ => "utility"
    };
}
