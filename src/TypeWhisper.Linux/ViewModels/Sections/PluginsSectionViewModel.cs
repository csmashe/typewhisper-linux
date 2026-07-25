using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// ReSharper disable SuspiciousTypeConversion.Global
// ReSharper disable UnusedParameterInPartialMethod

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PluginsSectionViewModel : ObservableObject
{
    private static readonly TimeSpan s_defaultPluginBoundaryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_defaultPluginValidationTimeout = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> s_transcriptionPluginIds =
    [
        "com.typewhisper.assemblyai",
        "com.typewhisper.cloudflare-asr",
        "com.typewhisper.deepgram",
        "com.typewhisper.gladia",
        "com.typewhisper.google-cloud-stt",
        "com.typewhisper.openai",
        "com.typewhisper.qwen3-stt",
        "com.typewhisper.sherpa-onnx",
        "com.typewhisper.soniox",
        "com.typewhisper.speechmatics",
        "com.typewhisper.voxtral",
        "com.typewhisper.whisper-cpp",
    ];

    private static readonly HashSet<string> s_llmPluginIds =
    [
        "com.typewhisper.cerebras",
        "com.typewhisper.claude",
        "com.typewhisper.cohere",
        "com.typewhisper.fireworks",
        "com.typewhisper.gemini",
        "com.typewhisper.gemma-local",
        "com.typewhisper.groq",
        "com.typewhisper.openai-compatible",
        "com.typewhisper.openrouter",
    ];

    private static readonly HashSet<string> s_actionPluginIds =
    [
        "com.typewhisper.linear",
        "com.typewhisper.obsidian",
        "com.typewhisper.script",
        "com.typewhisper.webhook",
    ];

    private static readonly HashSet<string> s_memoryPluginIds =
    [
        "com.typewhisper.file-memory",
        "com.typewhisper.openai-vector-memory",
    ];

    private static readonly HashSet<string> s_utilityPluginIds =
    [
        "com.typewhisper.openai-compatible",
    ];

    private readonly IErrorLogService? _errorLog;
    private readonly Dictionary<string, LoadedPlugin> _pluginById = [];
    private readonly TimeSpan _pluginBoundaryTimeout;
    private readonly TimeSpan _pluginValidationTimeout;
    private readonly PluginManager _pluginManager;

    [ObservableProperty]
    private string _headerSummary = "";

    [ObservableProperty]
    private string _summary = "";

    public PluginsSectionViewModel(PluginManager pluginManager, IErrorLogService? errorLog = null)
        : this(pluginManager, errorLog, s_defaultPluginBoundaryTimeout)
    {
    }

    internal PluginsSectionViewModel(
        PluginManager pluginManager,
        IErrorLogService? errorLog,
        TimeSpan pluginBoundaryTimeout,
        TimeSpan? pluginValidationTimeout = null
    )
    {
        _pluginManager = pluginManager;
        _errorLog = errorLog;
        _pluginBoundaryTimeout = pluginBoundaryTimeout;
        if (_pluginBoundaryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pluginBoundaryTimeout),
                "The plugin boundary timeout must be greater than zero."
            );
        }

        _pluginValidationTimeout = pluginValidationTimeout ?? s_defaultPluginValidationTimeout;
        if (_pluginValidationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pluginValidationTimeout),
                "The plugin validation timeout must be greater than zero."
            );
        }

        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<PluginCategoryGroup> PluginGroups { get; } = [];
    public ObservableCollection<PluginFailureRow> LoadFailures { get; } = [];
    public bool HasLoadFailures => LoadFailures.Count > 0;

    /// <summary>
    ///     Re-polls providers so newly pulled models appear when a model dropdown opens, without
    ///     requiring "Validate". Debounce/guard live in <see cref="PluginManager" />.
    /// </summary>
    public Task RefreshProviderModelsAsync()
    {
        return _pluginManager.RefreshProviderModelsAsync();
    }

    private void Refresh()
    {
        // Preserve expanded state across rebuilds so the user doesn't lose their open settings panel.
        var expandedPluginId = PluginGroups
            .SelectMany(group => group.Plugins)
            .FirstOrDefault(plugin => plugin.IsExpanded)
            ?.Id;

        PluginGroups.Clear();
        _pluginById.Clear();

        var plugins = new List<PluginRow>();
        foreach (var plugin in _pluginManager.AllPlugins)
        {
            _pluginById[plugin.Manifest.Id] = plugin;

            try
            {
                var loc = new PluginLocalization(plugin.PluginDirectory);
                var hasExpandableSettings = false;
                var settingsDefinitionFailed = false;

                if (plugin.Instance is IPluginSettingsProvider settingsProvider)
                {
                    var definitions = TryInvokePluginBoundary(
                        plugin,
                        "read setting definitions",
                        () => settingsProvider.GetSettingDefinitions().ToList()
                    );
                    if (definitions.IsSuccess)
                    {
                        hasExpandableSettings = definitions.Value!.Count > 0;
                    }
                    else
                    {
                        settingsDefinitionFailed = true;
                    }
                }

                if (
                    plugin.Instance
                    is IPluginCollectionSettingsProvider collectionSettingsProvider
                )
                {
                    var definitions = TryInvokePluginBoundary(
                        plugin,
                        "read collection definitions",
                        () => collectionSettingsProvider.GetCollectionDefinitions().ToList()
                    );
                    if (definitions.IsSuccess)
                    {
                        hasExpandableSettings |= definitions.Value!.Count > 0;
                    }
                    else
                    {
                        settingsDefinitionFailed = true;
                    }
                }

                var row = new PluginRow(
                    this,
                    plugin.Manifest.Id,
                    LocalizeManifest(loc, "Manifest.Name", plugin.Manifest.Name),
                    plugin.Manifest.Version,
                    LocalizeManifest(
                        loc,
                        "Manifest.Description",
                        plugin.Manifest.Description ?? ""
                    ),
                    InferCategory(plugin.Manifest),
                    InferIsLocal(plugin.Manifest),
                    hasExpandableSettings || settingsDefinitionFailed,
                    _pluginManager.IsEnabled(plugin.Manifest.Id)
                );

                if (settingsDefinitionFailed)
                {
                    MarkSettingsLoadFailed(row);
                }

                plugins.Add(row);
            }
            catch (Exception ex)
            {
                ReportPluginBoundaryFailure(plugin, "build settings card", ex);
                var row = new PluginRow(
                    this,
                    plugin.Manifest.Id,
                    plugin.Manifest.Name,
                    plugin.Manifest.Version,
                    plugin.Manifest.Description ?? "",
                    InferCategory(plugin.Manifest),
                    InferIsLocal(plugin.Manifest),
                    plugin.Instance is IPluginSettingsProvider
                        or IPluginCollectionSettingsProvider,
                    _pluginManager.IsEnabled(plugin.Manifest.Id)
                );
                MarkSettingsLoadFailed(row);
                plugins.Add(row);
            }
        }

        plugins = plugins
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
            LoadFailures.Add(
                new PluginFailureRow(Path.GetFileName(failure.PluginDirectory), failure.Message)
            );
        }

        OnPropertyChanged(nameof(HasLoadFailures));

        Summary = Loc.Instance.GetString("Plugins.SummaryLoaded", plugins.Count);
        if (LoadFailures.Count > 0)
        {
            Summary += Loc.Instance.GetString("Plugins.SummaryFailed", LoadFailures.Count);
        }

        var enabledCount = plugins.Count(p => p.IsEnabled);
        HeaderSummary = Loc.Instance.GetString("Plugins.HeaderSummary", plugins.Count, enabledCount);

        if (expandedPluginId is null)
        {
            return;
        }

        var expandedPlugin = PluginGroups
            .SelectMany(group => group.Plugins)
            .FirstOrDefault(plugin => plugin.Id == expandedPluginId);
        if (expandedPlugin is null)
        {
            return;
        }

        expandedPlugin.IsExpanded = true;
        BeginObservedSettingsLoad(expandedPlugin);
    }

    [RelayCommand]
    private async Task ToggleEnabled(PluginRow row)
    {
        if (row.IsEnabled)
        {
            await _pluginManager.DisablePluginAsync(row.Id);
        }
        else
        {
            await _pluginManager.EnablePluginAsync(row.Id);
        }
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
        {
            other.IsExpanded = false;
        }

        row.IsExpanded = true;
        await LoadPluginSettingsAsync(row);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync(PluginRow row)
    {
        if (!_pluginById.TryGetValue(row.Id, out var loaded))
        {
            return;
        }

        // A plugin can implement both interfaces (e.g. OpenAiCompatiblePlugin), so check each
        // independently rather than via a mutually-exclusive switch — otherwise only the first
        // matching kind of settings would ever persist.
        // ReSharper disable once ConvertIfStatementToSwitchStatement — see above; the checks are independent.
        if (loaded.Instance is IPluginSettingsProvider provider)
        {
            if (!await TrySaveFlatSettingsAsync(row, loaded, provider))
            {
                return;
            }
        }

        if (loaded.Instance is IPluginCollectionSettingsProvider collectionProvider)
        {
            foreach (var collection in row.Collections)
            {
                var items = collection
                    .Items.Select(item => new PluginCollectionItem(
                        item.Fields.ToDictionary(field => field.Key, string? (field) => field.Value)
                    ))
                    .ToList();

                var setResult = await TryInvokePluginBoundaryAsync(
                    loaded,
                    $"save collection '{collection.Key}'",
                    ct => collectionProvider.SetItemsAsync(collection.Key, items, ct)
                );
                if (!setResult.IsSuccess || setResult.Value is null)
                {
                    if (setResult.IsSuccess)
                    {
                        ReportPluginBoundaryFailure(
                            loaded,
                            $"save collection '{collection.Key}'",
                            new InvalidOperationException(
                                "The plugin returned no collection validation result."
                            )
                        );
                    }

                    row.Status = Loc.Instance["Plugins.SettingsSaveFailed"];
                    await LoadPluginSettingsAsync(row, true);
                    return;
                }

                if (setResult.Value.IsSuccess)
                {
                    continue;
                }

                row.Status = setResult.Value.Message;
                return;
            }
        }

        row.Status = Loc.Instance["Plugins.SettingsSaved"];
    }

    [RelayCommand]
    private async Task ValidateSettingsAsync(PluginRow row)
    {
        if (!_pluginById.TryGetValue(row.Id, out var loaded))
        {
            return;
        }

        if (loaded.Instance is not IPluginSettingsProvider provider)
        {
            return;
        }

        if (!await TrySaveFlatSettingsAsync(row, loaded, provider))
        {
            return;
        }

        var validation = await TryInvokePluginBoundaryAsync(
            loaded,
            "validate settings",
            provider.ValidateAsync,
            _pluginValidationTimeout
        );
        if (!validation.IsSuccess)
        {
            row.Status = Loc.Instance["Plugins.UnableToLoadSettings"];
            return;
        }

        row.Status =
            validation.Value?.Message ?? Loc.Instance["Plugins.NoValidationAvailable"];
        await LoadPluginSettingsAsync(row, true);
    }

    private async Task<bool> TrySaveFlatSettingsAsync(
        PluginRow row,
        LoadedPlugin loaded,
        IPluginSettingsProvider provider
    )
    {
        foreach (var field in row.SettingFields)
        {
            var setResult = await TryInvokePluginBoundaryAsync(
                loaded,
                $"save setting '{field.Key}'",
                async ct =>
                {
                    await provider.SetSettingValueAsync(field.Key, field.Value, ct)
                        .ConfigureAwait(false);
                    return true;
                }
            );
            if (!setResult.IsSuccess)
            {
                row.Status = Loc.Instance["Plugins.SettingsSaveFailed"];
                await LoadPluginSettingsAsync(row, true);
                return false;
            }
        }

        return true;
    }

    private async Task LoadPluginSettingsAsync(PluginRow row, bool preserveStatus = false)
    {
        row.SettingFields.Clear();
        row.Collections.Clear();
        row.CanEditSettings = false;
        row.CanValidateSettings = false;

        if (!_pluginById.TryGetValue(row.Id, out var loaded))
        {
            row.Status = Loc.Instance["Plugins.UnableToLoadSettings"];
            return;
        }

        var flatProvider = loaded.Instance as IPluginSettingsProvider;
        var collectionProvider = loaded.Instance as IPluginCollectionSettingsProvider;

        if (flatProvider is null && collectionProvider is null)
        {
            row.Status = Loc.Instance["Plugins.NoHostNeutralSettings"];
            return;
        }

        if (flatProvider is not null)
        {
            var definitions = await TryInvokePluginBoundaryAsync(
                loaded,
                "read setting definitions",
                _ => Task.FromResult(flatProvider.GetSettingDefinitions().ToList())
            );
            if (!definitions.IsSuccess)
            {
                MarkSettingsLoadFailed(row, preserveStatus);
                return;
            }

            try
            {
                foreach (var definition in definitions.Value!)
                {
                    var settingValue = await TryInvokePluginBoundaryAsync(
                        loaded,
                        $"read setting '{definition.Key}'",
                        ct => flatProvider.GetSettingValueAsync(definition.Key, ct)
                    );
                    if (!settingValue.IsSuccess)
                    {
                        MarkSettingsLoadFailed(row, preserveStatus);
                        return;
                    }

                    row.SettingFields.Add(
                        new PluginSettingFieldRow(
                            definition.Key,
                            definition.Label,
                            definition.Description ?? string.Empty,
                            definition.Placeholder ?? string.Empty,
                            definition.Options ?? [],
                            definition.IsSecret,
                            definition.Kind,
                            settingValue.Value ?? string.Empty
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                ReportPluginBoundaryFailure(loaded, "read setting definitions", ex);
                MarkSettingsLoadFailed(row, preserveStatus);
                return;
            }
        }

        if (collectionProvider is not null)
        {
            var definitions = await TryInvokePluginBoundaryAsync(
                loaded,
                "read collection definitions",
                _ => Task.FromResult(collectionProvider.GetCollectionDefinitions().ToList())
            );
            if (!definitions.IsSuccess)
            {
                MarkSettingsLoadFailed(row, preserveStatus);
                return;
            }

            try
            {
                foreach (var definition in definitions.Value!)
                {
                    var collectionItems = await TryInvokePluginBoundaryAsync(
                        loaded,
                        $"read collection '{definition.Key}'",
                        async ct =>
                            (
                                await collectionProvider
                                    .GetItemsAsync(definition.Key, ct)
                                    .ConfigureAwait(false)
                            ).ToList()
                    );
                    if (!collectionItems.IsSuccess)
                    {
                        MarkSettingsLoadFailed(row, preserveStatus);
                        return;
                    }

                    row.Collections.Add(
                        new PluginCollectionRow(definition, row, collectionItems.Value!)
                    );
                }
            }
            catch (Exception ex)
            {
                ReportPluginBoundaryFailure(loaded, "read collection settings", ex);
                MarkSettingsLoadFailed(row, preserveStatus);
                return;
            }
        }

        var hasFlatFields = row.SettingFields.Count > 0;
        var hasCollections = row.Collections.Count > 0;

        row.CanEditSettings = hasFlatFields || hasCollections;
        row.CanValidateSettings = flatProvider is not null;
        if (!preserveStatus)
        {
            row.Status =
                hasFlatFields || hasCollections
                    ? Loc.Instance["Plugins.EditValuesHint"]
                    : Loc.Instance["Plugins.NoEditableFields"];
        }
    }

    private void BeginObservedSettingsLoad(PluginRow row)
    {
        var loadTask = ObserveSettingsLoadAsync(row);
        _ = loadTask.ContinueWith(
            completedTask =>
                Trace.WriteLine(
                    $"[PluginsSectionViewModel] Failed to handle settings load for plugin "
                        + $"'{row.Id}': {completedTask.Exception!.GetBaseException().Message}"
                ),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private async Task ObserveSettingsLoadAsync(PluginRow row)
    {
        try
        {
            await LoadPluginSettingsAsync(row);
        }
        catch (Exception ex)
        {
            if (_pluginById.TryGetValue(row.Id, out var loaded))
            {
                ReportPluginBoundaryFailure(loaded, "load settings", ex);
            }
            else
            {
                Trace.WriteLine(
                    $"[PluginsSectionViewModel] Failed to load settings for plugin "
                        + $"'{row.Id}': {ex}"
                );
            }

            MarkSettingsLoadFailed(row);
        }
    }

    private void MarkSettingsLoadFailed(PluginRow row, bool preserveStatus = false)
    {
        row.SettingFields.Clear();
        row.Collections.Clear();
        row.CanEditSettings = false;
        row.CanValidateSettings = false;

        if (!preserveStatus)
        {
            row.Status = Loc.Instance["Plugins.UnableToLoadSettings"];
        }
    }

    private PluginBoundaryResult<T> TryInvokePluginBoundary<T>(
        LoadedPlugin plugin,
        string operation,
        Func<T> boundary
    )
    {
        return TryInvokePluginBoundaryAsync(
                plugin,
                operation,
                _ => Task.FromResult(boundary())
            )
            .GetAwaiter()
            .GetResult();
    }

    private async Task<PluginBoundaryResult<T>> TryInvokePluginBoundaryAsync<T>(
        LoadedPlugin plugin,
        string operation,
        Func<CancellationToken, Task<T>> boundary,
        TimeSpan? overrideTimeout = null
    )
    {
        var timeoutDuration = overrideTimeout ?? _pluginBoundaryTimeout;
        var boundaryTask = Task.Run(
            () => boundary(CancellationToken.None),
            CancellationToken.None
        );
        var completedTask = await Task.WhenAny(
                boundaryTask,
                Task.Delay(timeoutDuration)
            )
            .ConfigureAwait(false);

        if (completedTask != boundaryTask)
        {
            var timeout = new TimeoutException(
                $"The operation timed out after "
                    + $"{timeoutDuration.TotalSeconds:0.###} seconds."
            );
            ReportPluginBoundaryFailure(plugin, operation, timeout);
            ObserveLatePluginBoundary(
                boundaryTask,
                plugin.Manifest.Id,
                operation
            );
            return PluginBoundaryResult<T>.Failure;
        }

        try
        {
            var value = await boundaryTask.ConfigureAwait(false);
            return new PluginBoundaryResult<T>(true, value);
        }
        catch (Exception ex)
        {
            ReportPluginBoundaryFailure(plugin, operation, ex);
            return PluginBoundaryResult<T>.Failure;
        }
    }

    private void ReportPluginBoundaryFailure(
        LoadedPlugin plugin,
        string operation,
        Exception exception
    )
    {
        var failure = exception.GetBaseException();
        var message =
            $"Plugin '{plugin.Manifest.Name}' failed to {operation}: {failure.Message}";
        _errorLog?.AddEntry(message, ErrorCategory.Plugin);
        Trace.WriteLine($"[PluginsSectionViewModel] {message}");
    }

    private static void ObserveLatePluginBoundary(
        Task boundaryTask,
        string pluginId,
        string operation
    )
    {
        _ = boundaryTask.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    Trace.WriteLine(
                        $"[PluginsSectionViewModel] {operation} for plugin '{pluginId}' "
                            + $"faulted after timeout: "
                            + completedTask.Exception!.GetBaseException().Message
                    );
                }
                else if (completedTask.IsCanceled)
                {
                    Trace.WriteLine(
                        $"[PluginsSectionViewModel] {operation} for plugin '{pluginId}' "
                            + "canceled after timeout"
                    );
                }
                else
                {
                    Trace.WriteLine(
                        $"[PluginsSectionViewModel] {operation} for plugin '{pluginId}' "
                            + "completed after timeout"
                    );
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private readonly record struct PluginBoundaryResult<T>(bool IsSuccess, T? Value)
    {
        public static PluginBoundaryResult<T> Failure => new(false, default);
    }

    // Plugin card name/description come from manifest.json (single-language).
    // Resolve them through the plugin's own catalog so they follow the UI
    // language, falling back to the manifest literal when the catalog has no
    // entry (third-party plugins, or keys not yet translated). PluginLocalization
    // returns the key itself on a miss, so an unchanged key signals "no entry".
    private static string LocalizeManifest(PluginLocalization loc, string key, string fallback)
    {
        var localized = loc.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal) ? fallback : localized;
    }

    // Local-vs-cloud inference is shared with the history Inspect provenance badges.
    private static bool InferIsLocal(PluginManifest manifest) =>
        PluginLocalityClassifier.IsLocal(manifest);

    // Manifest Category takes precedence; fall back to known-ID lists then keyword heuristics.
    private static string? InferCategory(PluginManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Category))
        {
            return manifest.Category;
        }

        var id = manifest.Id.Trim().ToLowerInvariant();
        if (s_transcriptionPluginIds.Contains(id))
        {
            return "transcription";
        }

        if (s_llmPluginIds.Contains(id))
        {
            return "llm";
        }

        if (s_actionPluginIds.Contains(id))
        {
            return "action";
        }

        if (s_memoryPluginIds.Contains(id))
        {
            return "memory";
        }

        if (s_utilityPluginIds.Contains(id))
        {
            return "utility";
        }

        var combined = $"{manifest.Name} {manifest.Description}".ToLowerInvariant();
        if (
            combined.Contains("transcription")
            || combined.Contains("speech-to-text")
            || combined.Contains("speech to text")
            || combined.Contains("asr")
        )
        {
            return "transcription";
        }

        if (
            combined.Contains("llm")
            || combined.Contains("prompt")
            || combined.Contains("inference")
            || combined.Contains("multi-model")
        )
        {
            return "llm";
        }

        if (combined.Contains("memory"))
        {
            return "memory";
        }

        if (
            combined.Contains("issue")
            || combined.Contains("obsidian")
            || combined.Contains("webhook")
            || combined.Contains("script")
        )
        {
            return "action";
        }

        return "utility";
    }
}

public sealed class PluginCategoryGroup
{
    public PluginCategoryGroup(string title, IEnumerable<PluginRow> plugins)
    {
        Title = title;
        Plugins = new ObservableCollection<PluginRow>(plugins);
    }

    public string Title { get; }
    public ObservableCollection<PluginRow> Plugins { get; }
}

public partial class PluginRow : ObservableObject
{
    [ObservableProperty]
    private bool _canEditSettings;

    [ObservableProperty]
    private bool _canValidateSettings;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _status = Loc.Instance["Plugins.ExpandToEdit"];

    public PluginRow(
        PluginsSectionViewModel? owner,
        string id,
        string name,
        string version,
        string description,
        string? category,
        bool isLocal,
        bool hasExpandableSettings,
        bool isEnabled
    )
    {
        Owner = owner;
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        IsLocal = isLocal;
        HasExpandableSettings = hasExpandableSettings;
        IsEnabled = isEnabled;

        var descriptor = PluginCategories.Resolve(category);
        CategoryKey = descriptor.Key;
        CategoryLabel = descriptor.DisplayName;
        CategorySortOrder = descriptor.SortOrder;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string CategoryKey { get; }
    public string CategoryLabel { get; }
    public int CategorySortOrder { get; }
    private bool IsLocal { get; }
    public string LocationBadge =>
        IsLocal ? Loc.Instance["Plugins.BadgeLocal"] : Loc.Instance["Plugins.BadgeCloud"];
    public string StatusBadge =>
        IsEnabled ? Loc.Instance["Plugins.BadgeEnabled"] : Loc.Instance["Plugins.BadgeDisabled"];
    public string LocationBadgeBackground => IsLocal ? "#1B2F24" : "#1A3453";
    public string LocationBadgeBorder => IsLocal ? "#2F5E45" : "#2E5B89";
    public string LocationBadgeForeground => IsLocal ? "#D8F3E5" : "#D6E7FF";
    public string StatusBadgeBackground => IsEnabled ? "#173222" : "#3A1F1F";
    public string StatusBadgeBorder => IsEnabled ? "#2F7D4E" : "#8A3A3A";
    public string StatusBadgeForeground => IsEnabled ? "#D9FBE7" : "#FFD9D9";

    public string Monogram =>
        string.Concat(
            Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => char.IsLetterOrDigit(part[0]))
                .Take(2)
                .Select(part => char.ToUpperInvariant(part[0]))
        );

    public string ExpansionGlyph => IsExpanded ? "⌃" : "⌄";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasExpandableSettings { get; }
    public ObservableCollection<PluginSettingFieldRow> SettingFields { get; } = [];
    public ObservableCollection<PluginCollectionRow> Collections { get; } = [];

    public PluginsSectionViewModel? Owner { get; }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpansionGlyph));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(StatusBadgeBackground));
        OnPropertyChanged(nameof(StatusBadgeBorder));
        OnPropertyChanged(nameof(StatusBadgeForeground));
    }
}

public sealed record PluginFailureRow(string FolderName, string Message);

public sealed partial class PluginSettingFieldRow : ObservableObject
{
    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private PluginSettingOption? _selectedOption;

    // Prevents infinite cycling: Value↔BoolValue two-way sync would otherwise loop.
    private bool _syncingBoolValue;

    [ObservableProperty]
    private string _value;

    public PluginSettingFieldRow(
        string key,
        string label,
        string description,
        string placeholder,
        IReadOnlyList<PluginSettingOption> options,
        bool isSecret,
        PluginSettingKind kind,
        string value
    )
    {
        Key = key;
        Label = label;
        Description = description;
        Placeholder = placeholder;
        Options = options;
        Kind = ResolveKind(kind, options, isSecret);
        _value = value;
        _selectedOption = Options.FirstOrDefault(o => o.Value == value) ?? (Options.Count > 0 ? Options[0] : null);
        if (_selectedOption is not null && string.IsNullOrEmpty(_value))
        {
            _value = _selectedOption.Value;
        }

        _boolValue = string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public string Key { get; }
    public string Label { get; }
    public string Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string Placeholder { get; }
    public IReadOnlyList<PluginSettingOption> Options { get; }
    private bool HasOptions => Options.Count > 0;
    public PluginSettingKind Kind { get; }

    public bool IsTextKind => Kind == PluginSettingKind.Text;
    public bool IsSecretKind => Kind == PluginSettingKind.Secret;
    public bool IsDropdownKind => Kind == PluginSettingKind.Dropdown;
    public bool IsBooleanKind => Kind == PluginSettingKind.Boolean;
    public bool IsMultilineKind => Kind == PluginSettingKind.Multiline;

    public bool IsHidden => Key.StartsWith("__", StringComparison.Ordinal);

    private static PluginSettingKind ResolveKind(
        PluginSettingKind kind,
        IReadOnlyList<PluginSettingOption> options,
        bool isSecret
    )
    {
        if (kind != PluginSettingKind.Auto)
        {
            return kind;
        }

        if (options.Count > 0)
        {
            return PluginSettingKind.Dropdown;
        }

        return isSecret ? PluginSettingKind.Secret : PluginSettingKind.Text;
    }

    partial void OnSelectedOptionChanged(PluginSettingOption? value)
    {
        if (value is not null && _value != value.Value)
        {
            Value = value.Value;
        }
    }

    partial void OnValueChanged(string value)
    {
        if (HasOptions)
        {
            var option = Options.FirstOrDefault(o => o.Value == value);
            if (!Equals(_selectedOption, option))
            {
                SelectedOption = option;
            }
        }

        if (_syncingBoolValue)
        {
            return;
        }

        _syncingBoolValue = true;
        BoolValue = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        _syncingBoolValue = false;
    }

    partial void OnBoolValueChanged(bool value)
    {
        if (_syncingBoolValue)
        {
            return;
        }

        _syncingBoolValue = true;
        Value = value ? "true" : "false";
        _syncingBoolValue = false;
    }
}

internal sealed record PluginCategoryInfo(string Key, string DisplayName, int SortOrder);

internal static class PluginCategories
{
    public static PluginCategoryInfo Resolve(string? rawCategory)
    {
        return Normalize(rawCategory) switch
        {
            "transcription" => new PluginCategoryInfo(
                "transcription",
                Loc.Instance["Plugins.CategoryTranscription"],
                0
            ),
            "llm" => new PluginCategoryInfo("llm", Loc.Instance["Plugins.CategoryLlm"], 1),
            "post-processing" => new PluginCategoryInfo(
                "post-processing",
                Loc.Instance["Plugins.CategoryPostProcessing"],
                2
            ),
            "action" => new PluginCategoryInfo("action", Loc.Instance["Plugins.CategoryAction"], 3),
            "memory" => new PluginCategoryInfo("memory", Loc.Instance["Plugins.CategoryMemory"], 4),
            _ => new PluginCategoryInfo("utility", Loc.Instance["Plugins.CategoryUtility"], 5),
        };
    }

    private static string Normalize(string? rawCategory)
    {
        return rawCategory?.Trim().ToLowerInvariant() switch
        {
            "transcription" => "transcription",
            "llm" => "llm",
            "postprocessing" or "post-processing" or "postprocessor" or "post-processor" =>
                "post-processing",
            "action" => "action",
            "memory" => "memory",
            _ => "utility",
        };
    }
}
