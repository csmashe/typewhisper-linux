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
        RebuildPluginRows(PluginListRefreshKind.Initial);
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
        RebuildPluginRows(PluginListRefreshKind.Ambient);
    }

    private void RebuildPluginRows(PluginListRefreshKind refreshKind)
    {
        // Preserve expanded state across rebuilds so the user doesn't lose their open settings panel.
        var existingRows = PluginGroups
            .SelectMany(group => group.Plugins)
            .DistinctBy(plugin => plugin.Id)
            .ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        var expandedPluginId = existingRows.Values.FirstOrDefault(plugin => plugin.IsExpanded)?.Id;

        PluginGroups.Clear();
        _pluginById.Clear();

        var plugins = new List<PluginRow>();
        foreach (var plugin in _pluginManager.AllPlugins)
        {
            _pluginById[plugin.Manifest.Id] = plugin;

            if (
                refreshKind == PluginListRefreshKind.Ambient
                && existingRows.TryGetValue(plugin.Manifest.Id, out var existingRow)
                && existingRow.HasUnsavedSettings
                && ReferenceEquals(existingRow.LoadedPlugin, plugin)
            )
            {
                existingRow.IsEnabled = _pluginManager.IsEnabled(plugin.Manifest.Id);
                plugins.Add(existingRow);
                continue;
            }

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
                    plugin.Metadata,
                    hasExpandableSettings || settingsDefinitionFailed,
                    _pluginManager.IsEnabled(plugin.Manifest.Id)
                ) { LoadedPlugin = plugin };

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
                    plugin.Metadata,
                    plugin.Instance is IPluginSettingsProvider
                        or IPluginCollectionSettingsProvider,
                    _pluginManager.IsEnabled(plugin.Manifest.Id)
                ) { LoadedPlugin = plugin };
                MarkSettingsLoadFailed(row);
                plugins.Add(row);
            }
        }

        plugins = plugins
            .OrderBy(p => p.CategorySortOrder)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var categoryMemberships = plugins
            .SelectMany(plugin =>
                plugin.Categories.Select(category =>
                    new
                    {
                        Plugin = plugin,
                        Category = PluginCategories.Resolve(category),
                    }
                )
            )
            .OrderBy(item => item.Category.SortOrder)
            .ThenBy(item => item.Plugin.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in categoryMemberships.GroupBy(item => item.Category.Key))
        {
            var categoryPlugins = group.Select(item => item.Plugin).ToList();
            var categoryLabel = group.First().Category.DisplayName;
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
        BeginObservedSettingsLoad(
            expandedPlugin,
            refreshKind == PluginListRefreshKind.Ambient
                ? SettingsReloadKind.PreserveDraft
                : SettingsReloadKind.ResetBaseline
        );
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
        await LoadPluginSettingsAsync(row, SettingsReloadKind.ResetBaseline);
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
                        item.Fields.ToDictionary(
                            field => field.Key,
                            string? (field) =>
                                field is { IsSecretKind: true, IsUserModified: false }
                                    ? null
                                    : field.Value
                        )
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
                    await ReloadCurrentVisibleRowAsync(row, loaded, true);
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
        await ReloadCurrentVisibleRowAsync(row, loaded, true);
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
        await ReloadCurrentVisibleRowAsync(row, loaded, true);
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
            // ReSharper disable once InvertIf -- guard clause; inverting would bury the failure path.
            if (!setResult.IsSuccess)
            {
                row.Status = Loc.Instance["Plugins.SettingsSaveFailed"];
                await ReloadCurrentVisibleRowAsync(row, loaded, true);
                return false;
            }
        }

        return true;
    }

    private async Task ReloadCurrentVisibleRowAsync(
        PluginRow commandRow,
        LoadedPlugin loaded,
        bool preserveStatus
    )
    {
        if (
            !_pluginById.TryGetValue(commandRow.Id, out var currentLoaded)
            || !ReferenceEquals(currentLoaded, loaded)
        )
        {
            return;
        }

        var currentRow = PluginGroups
            .SelectMany(group => group.Plugins)
            .FirstOrDefault(row => ReferenceEquals(row.LoadedPlugin, loaded));
        if (currentRow is null)
        {
            return;
        }

        if (!ReferenceEquals(currentRow, commandRow))
        {
            currentRow.Status = commandRow.Status;
        }

        await LoadPluginSettingsAsync(
            currentRow,
            SettingsReloadKind.ResetBaseline,
            preserveStatus
        );
    }

    private async Task LoadPluginSettingsAsync(
        PluginRow row,
        SettingsReloadKind reloadKind,
        bool preserveStatus = false
    )
    {
        if (
            reloadKind == SettingsReloadKind.PreserveDraft
            && row.HasUnsavedSettings
        )
        {
            return;
        }

        row.SettingFields.Clear();
        row.Collections.Clear();
        row.CanEditSettings = false;
        row.CanValidateSettings = false;

        if (!_pluginById.TryGetValue(row.Id, out var loaded))
        {
            row.Status = Loc.Instance["Plugins.UnableToLoadSettings"];
            row.CaptureSettingsBaseline();
            return;
        }

        var flatProvider = loaded.Instance as IPluginSettingsProvider;
        var collectionProvider = loaded.Instance as IPluginCollectionSettingsProvider;

        if (flatProvider is null && collectionProvider is null)
        {
            row.Status = Loc.Instance["Plugins.NoHostNeutralSettings"];
            row.CaptureSettingsBaseline();
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

        row.CaptureSettingsBaseline();
    }

    private void BeginObservedSettingsLoad(PluginRow row, SettingsReloadKind reloadKind)
    {
        var loadTask = ObserveSettingsLoadAsync(row, reloadKind);
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

    private async Task ObserveSettingsLoadAsync(
        PluginRow row,
        SettingsReloadKind reloadKind
    )
    {
        try
        {
            await LoadPluginSettingsAsync(row, reloadKind);
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

    private static void MarkSettingsLoadFailed(PluginRow row, bool preserveStatus = false)
    {
        row.SettingFields.Clear();
        row.Collections.Clear();
        row.CanEditSettings = false;
        row.CanValidateSettings = false;
        row.CaptureSettingsBaseline();

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

    private enum PluginListRefreshKind
    {
        Initial,
        Ambient,
    }

    private enum SettingsReloadKind
    {
        PreserveDraft,
        ResetBaseline,
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
    private PluginSettingsDraftEntry[]? _settingsBaseline;

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
        PluginMetadataDescriptor metadata,
        bool hasExpandableSettings,
        bool isEnabled
    )
    {
        Owner = owner;
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        NetworkAccess = metadata.NetworkAccess;
        Categories = metadata.Categories;
        HasExpandableSettings = hasExpandableSettings;
        IsEnabled = isEnabled;

        var descriptor = Categories
            .Select(PluginCategories.Resolve)
            .OrderBy(category => category.SortOrder)
            .First();
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
    public IReadOnlySet<PluginCategory> Categories { get; }
    public PluginNetworkAccess NetworkAccess { get; }
    public bool RanLocally => NetworkAccess == PluginNetworkAccess.Local;
    public string LocationBadge => NetworkAccess switch
    {
        PluginNetworkAccess.Local => Loc.Instance["Plugins.BadgeLocal"],
        PluginNetworkAccess.Network => Loc.Instance["Plugins.BadgeCloud"],
        PluginNetworkAccess.Mixed => Loc.Instance["Plugins.BadgeMixed"],
        PluginNetworkAccess.UserControlled => Loc.Instance["Plugins.BadgeUserControlled"],
        _ => Loc.Instance["Plugins.BadgeCloud"],
    };
    public string StatusBadge =>
        IsEnabled ? Loc.Instance["Plugins.BadgeEnabled"] : Loc.Instance["Plugins.BadgeDisabled"];
    public string LocationBadgeBackground => NetworkAccess switch
    {
        PluginNetworkAccess.Local => "#1B2F24",
        PluginNetworkAccess.Mixed => "#30264A",
        PluginNetworkAccess.UserControlled => "#3A2C16",
        _ => "#1A3453",
    };
    public string LocationBadgeBorder => NetworkAccess switch
    {
        PluginNetworkAccess.Local => "#2F5E45",
        PluginNetworkAccess.Mixed => "#66518F",
        PluginNetworkAccess.UserControlled => "#80622C",
        _ => "#2E5B89",
    };
    public string LocationBadgeForeground => NetworkAccess switch
    {
        PluginNetworkAccess.Local => "#D8F3E5",
        PluginNetworkAccess.Mixed => "#E4D9FF",
        PluginNetworkAccess.UserControlled => "#FFE7B3",
        _ => "#D6E7FF",
    };
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
    internal LoadedPlugin? LoadedPlugin { get; init; }
    internal bool HasUnsavedSettings =>
        _settingsBaseline is not null
        && !_settingsBaseline.SequenceEqual(CaptureSettingsDraft());

    internal void CaptureSettingsBaseline()
    {
        _settingsBaseline = CaptureSettingsDraft();
    }

    private PluginSettingsDraftEntry[] CaptureSettingsDraft()
    {
        var entries = new List<PluginSettingsDraftEntry>();
        // ReSharper disable once LoopCanBeConvertedToQuery -- the loop index is captured into each entry; a query would need Select((_, i) => ...) and read worse.
        for (var fieldIndex = 0; fieldIndex < SettingFields.Count; fieldIndex++)
        {
            var field = SettingFields[fieldIndex];
            entries.Add(
                new PluginSettingsDraftEntry(
                    PluginSettingsDraftEntryKind.FlatField,
                    -1,
                    -1,
                    fieldIndex,
                    field.Key,
                    field.Value,
                    field.SelectedOption,
                    field is { IsSecretKind: true, IsUserModified: true }
                )
            );
        }

        for (var collectionIndex = 0; collectionIndex < Collections.Count; collectionIndex++)
        {
            var collection = Collections[collectionIndex];
            entries.Add(
                new PluginSettingsDraftEntry(
                    PluginSettingsDraftEntryKind.Collection,
                    collectionIndex,
                    -1,
                    -1,
                    collection.Key,
                    null,
                    null,
                    false
                )
            );

            for (var itemIndex = 0; itemIndex < collection.Items.Count; itemIndex++)
            {
                var item = collection.Items[itemIndex];
                // ReSharper disable once LoopCanBeConvertedToQuery -- the loop index is captured into each entry; a query would need Select((_, i) => ...) and read worse.
                for (var fieldIndex = 0; fieldIndex < item.Fields.Count; fieldIndex++)
                {
                    var field = item.Fields[fieldIndex];
                    entries.Add(
                        new PluginSettingsDraftEntry(
                            PluginSettingsDraftEntryKind.CollectionField,
                            collectionIndex,
                            itemIndex,
                            fieldIndex,
                            field.Key,
                            field.Value,
                            field.SelectedOption,
                            field is { IsSecretKind: true, IsUserModified: true }
                        )
                    );
                }
            }
        }

        return entries.ToArray();
    }

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

    private enum PluginSettingsDraftEntryKind
    {
        FlatField,
        Collection,
        CollectionField,
    }

    private sealed record PluginSettingsDraftEntry(
        PluginSettingsDraftEntryKind Kind,
        int CollectionIndex,
        int ItemIndex,
        int FieldIndex,
        string Key,
        string? Value,
        PluginSettingOption? SelectedOption,
        // A secret edited back to its baseline text still differs from an
        // untouched one, and only the draft carries that distinction until save.
        bool SecretModified
    );
}

public sealed record PluginFailureRow(string FolderName, string Message);

public sealed partial class PluginSettingFieldRow : ObservableObject
{
    private readonly PluginSettingOption[] _advertisedOptions;

    [ObservableProperty]
    private bool _boolValue;

    private readonly ObservableCollection<PluginSettingOption> _options;

    [ObservableProperty]
    private PluginSettingOption? _selectedOption;

    // Prevents infinite cycling: Value↔BoolValue two-way sync would otherwise loop.
    private bool _syncingBoolValue;

    // Keeps dropdown Value↔SelectedOption changes atomic and prevents recursive synchronization.
    private bool _syncingOptionValue;

    private PluginSettingOption? _unavailableOption;

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
        Kind = ResolveKind(kind, options, isSecret);
        _advertisedOptions = options.ToArray();
        _options = new ObservableCollection<PluginSettingOption>(_advertisedOptions);
        Options = new ReadOnlyObservableCollection<PluginSettingOption>(_options);
        _value = value;
        _selectedOption = _advertisedOptions.FirstOrDefault(option => option.Value == value);
        if (
            _selectedOption is null
            && Kind == PluginSettingKind.Dropdown
            && !string.IsNullOrEmpty(_value)
        )
        {
            _unavailableOption = new PluginSettingOption(_value, _value);
            _options.Insert(0, _unavailableOption);
            _selectedOption = _unavailableOption;
        }

        _selectedOption ??= Options.Count > 0 ? Options[0] : null;
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
    public ReadOnlyObservableCollection<PluginSettingOption> Options { get; }
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
        if (_syncingOptionValue)
        {
            return;
        }

        if (Kind != PluginSettingKind.Dropdown)
        {
            if (value is not null && _value != value.Value)
            {
                Value = value.Value;
            }

            return;
        }

        _syncingOptionValue = true;
        try
        {
            Value = value?.Value ?? string.Empty;
            RemoveUnavailableOptionIfDeselected(value);
        }
        finally
        {
            _syncingOptionValue = false;
        }
    }

    partial void OnValueChanged(string value)
    {
        // Only edits routed through the bound setter reach here; initial population
        // assigns the backing field directly and then resets this flag.
        IsUserModified = true;

        if (Kind == PluginSettingKind.Dropdown && !_syncingOptionValue)
        {
            _syncingOptionValue = true;
            try
            {
                SynchronizeDropdownSelection(value);
            }
            finally
            {
                _syncingOptionValue = false;
            }
        }
        else if (Kind != PluginSettingKind.Dropdown && Options.Count > 0)
        {
            var option = Options.FirstOrDefault(candidate => candidate.Value == value);
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

    private void SynchronizeDropdownSelection(string value)
    {
        var advertisedOption = _advertisedOptions.FirstOrDefault(
            option => option.Value == value
        );
        if (advertisedOption is not null)
        {
            SelectedOption = advertisedOption;
            RemoveUnavailableOptionIfDeselected(advertisedOption);
            return;
        }

        if (string.IsNullOrEmpty(value))
        {
            SelectedOption = null;
            RemoveUnavailableOptionIfDeselected(null);
            return;
        }

        if (_unavailableOption?.Value == value)
        {
            SelectedOption = _unavailableOption;
            return;
        }

        var previousUnavailableOption = _unavailableOption;
        _unavailableOption = new PluginSettingOption(value, value);
        _options.Insert(0, _unavailableOption);
        SelectedOption = _unavailableOption;
        if (previousUnavailableOption is not null)
        {
            _options.Remove(previousUnavailableOption);
        }
    }

    private void RemoveUnavailableOptionIfDeselected(PluginSettingOption? selectedOption)
    {
        if (
            _unavailableOption is null
            || ReferenceEquals(selectedOption, _unavailableOption)
        )
        {
            return;
        }

        var unavailableOption = _unavailableOption;
        _unavailableOption = null;
        _options.Remove(unavailableOption);
    }
}

internal sealed record PluginCategoryInfo(string Key, string DisplayName, int SortOrder);

internal static class PluginCategories
{
    public static PluginCategoryInfo Resolve(PluginCategory category)
    {
        return category switch
        {
            PluginCategory.Transcription => new PluginCategoryInfo(
                "transcription",
                Loc.Instance["Plugins.CategoryTranscription"],
                0
            ),
            PluginCategory.Llm => new PluginCategoryInfo(
                "llm",
                Loc.Instance["Plugins.CategoryLlm"],
                1
            ),
            PluginCategory.Tts => new PluginCategoryInfo(
                "tts",
                Loc.Instance["Plugins.CategoryTts"],
                2
            ),
            PluginCategory.PostProcessing => new PluginCategoryInfo(
                "post-processing",
                Loc.Instance["Plugins.CategoryPostProcessing"],
                3
            ),
            PluginCategory.Action => new PluginCategoryInfo(
                "action",
                Loc.Instance["Plugins.CategoryAction"],
                4
            ),
            PluginCategory.Memory => new PluginCategoryInfo(
                "memory",
                Loc.Instance["Plugins.CategoryMemory"],
                5
            ),
            PluginCategory.Integration => new PluginCategoryInfo(
                "integration",
                Loc.Instance["Plugins.CategoryIntegration"],
                6
            ),
            PluginCategory.Utility => new PluginCategoryInfo(
                "utility",
                Loc.Instance["Plugins.CategoryUtility"],
                7
            ),
            _ => new PluginCategoryInfo(
                "unknown",
                Loc.Instance["Plugins.CategoryUnknown"],
                8
            ),
        };
    }
}
