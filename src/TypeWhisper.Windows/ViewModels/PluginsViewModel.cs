using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

public partial class PluginsViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly PluginRegistryService _registryService;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<RegistryPluginItemViewModel> RegistryPlugins { get; } = [];
    public ObservableCollection<RegistryPluginCategoryGroupViewModel> MarketplaceGroups { get; } = [];
    public int InstalledPluginCount => Plugins.Count;
    public int EnabledPluginCount => Plugins.Count(static plugin => plugin.IsEnabled);
    public int MarketplacePluginCount => RegistryPlugins.Count;
    public string InstalledSummaryText => Loc.Instance.GetString("Plugins.InstalledSummaryFormat", InstalledPluginCount, EnabledPluginCount);
    public string MarketplaceSummaryText => Loc.Instance.GetString("Plugins.MarketplaceSummaryFormat", MarketplacePluginCount);

    [ObservableProperty] private bool _isLoadingRegistry;

    public PluginsViewModel(PluginManager pluginManager, PluginRegistryService registryService)
    {
        _pluginManager = pluginManager;
        _registryService = registryService;
        _pluginManager.PluginStateChanged += (_, _) =>
            Application.Current.Dispatcher.Invoke(RefreshPlugins);
        RefreshPlugins();
        _ = RefreshRegistryAsync();
    }

    private void RefreshPlugins()
    {
        // Preserve expanded state across refresh
        var expandedIds = Plugins.Where(p => p.IsExpanded).Select(p => p.Id).ToHashSet();

        Plugins.Clear();
        foreach (var plugin in _pluginManager.AllPlugins)
        {
            var isEnabled = _pluginManager.IsEnabled(plugin.Manifest.Id);
            var vm = new PluginItemViewModel(plugin, isEnabled, _pluginManager, _registryService);
            if (expandedIds.Contains(vm.Id))
                vm.IsExpanded = true;
            Plugins.Add(vm);
        }

        NotifyStateChanged();
    }

    [RelayCommand]
    private async Task RefreshRegistryAsync()
    {
        IsLoadingRegistry = true;

        try
        {
            var registry = await _registryService.FetchRegistryAsync();
            var registryItems = registry
                .Select(plugin => new RegistryPluginItemViewModel(plugin, _registryService))
                .OrderBy(plugin => plugin.CategorySortOrder)
                .ThenBy(plugin => plugin.Name)
                .ToList();

            RegistryPlugins.Clear();
            MarketplaceGroups.Clear();

            foreach (var plugin in registryItems)
            {
                RegistryPlugins.Add(plugin);
            }

            foreach (var group in registryItems
                         .GroupBy(plugin => plugin.CategoryKey)
                         .OrderBy(group => group.First().CategorySortOrder))
            {
                var first = group.First();
                MarketplaceGroups.Add(new RegistryPluginCategoryGroupViewModel(first.CategoryLabel, group));
            }

            NotifyStateChanged();
        }
        finally
        {
            IsLoadingRegistry = false;
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(InstalledPluginCount));
        OnPropertyChanged(nameof(EnabledPluginCount));
        OnPropertyChanged(nameof(MarketplacePluginCount));
        OnPropertyChanged(nameof(InstalledSummaryText));
        OnPropertyChanged(nameof(MarketplaceSummaryText));
    }
}

public partial class RegistryPluginCategoryGroupViewModel : ObservableObject
{
    public string DisplayName { get; }
    public ObservableCollection<RegistryPluginItemViewModel> Plugins { get; }
    public int Count => Plugins.Count;

    public RegistryPluginCategoryGroupViewModel(string displayName, IEnumerable<RegistryPluginItemViewModel> plugins)
    {
        DisplayName = displayName;
        Plugins = [.. plugins];
    }
}

public partial class PluginItemViewModel : ObservableObject
{
    private readonly LoadedPlugin _plugin;
    private readonly PluginManager _pluginManager;
    private readonly PluginRegistryService _registryService;

    public string Id => _plugin.Manifest.Id;
    public string Name => _plugin.Manifest.Name;
    public string Version => _plugin.Manifest.Version;
    public string? Author => _plugin.Manifest.Author;
    public string? Description => _plugin.Manifest.Description;
    public string IconEmoji => PluginIconHelper.GetIcon(Id);
    public string IconGradientStart => PluginIconHelper.GetGradientStart(Id);
    public string IconGradientEnd => PluginIconHelper.GetGradientEnd(Id);
    public string StatusLabel => IsEnabled ? Loc.Instance["Plugins.Enabled"] : Loc.Instance["Plugins.Disabled"];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private UserControl? _settingsView;
    [ObservableProperty] private bool _isExpanded;

    // Capability badges
    public bool IsTranscriptionProvider => _plugin.Instance is TypeWhisper.PluginSDK.ITranscriptionEnginePlugin;
    public bool IsLlmProvider => _plugin.Instance is TypeWhisper.PluginSDK.ILlmProviderPlugin;
    public bool IsPostProcessor => _plugin.Instance is TypeWhisper.PluginSDK.IPostProcessorPlugin;
    public bool IsActionProvider => _plugin.Instance is TypeWhisper.PluginSDK.IActionPlugin;
    public bool IsMemoryStorage => _plugin.Instance is TypeWhisper.PluginSDK.IMemoryStoragePlugin;

    public string Category => PluginMarketplaceCategories.Resolve(_plugin.Manifest.Category ?? DetectCategory()).DisplayName;

    public bool IsLocal => _plugin.Manifest.IsLocal;
    public string LocationBadge => IsLocal ? "Local" : "Cloud";

    public PluginItemViewModel(LoadedPlugin plugin, bool isEnabled, PluginManager pluginManager, PluginRegistryService registryService)
    {
        _plugin = plugin;
        _pluginManager = pluginManager;
        _registryService = registryService;
        _isEnabled = isEnabled;

        if (isEnabled)
            _settingsView = plugin.Instance.CreateSettingsView();
    }

    async partial void OnIsEnabledChanged(bool value)
    {
        if (value)
        {
            await _pluginManager.EnablePluginAsync(Id);
            SettingsView = _plugin.Instance.CreateSettingsView();
        }
        else
        {
            await _pluginManager.DisablePluginAsync(Id);
            SettingsView = null;
        }

        OnPropertyChanged(nameof(StatusLabel));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        // Lazy-load settings view when first expanded
        if (value && SettingsView is null && IsEnabled)
            SettingsView = _plugin.Instance.CreateSettingsView();
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var result = MessageBox.Show(
            Loc.Instance.GetString("Plugins.UninstallConfirm", Name),
            Loc.Instance["Plugins.UninstallTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        await _registryService.UninstallPluginAsync(Id);
    }

    private string DetectCategory() => _plugin.Instance switch
    {
        TypeWhisper.PluginSDK.ITranscriptionEnginePlugin => "transcription",
        TypeWhisper.PluginSDK.ILlmProviderPlugin => "llm",
        TypeWhisper.PluginSDK.IMemoryStoragePlugin => "memory",
        TypeWhisper.PluginSDK.IPostProcessorPlugin => "post-processing",
        TypeWhisper.PluginSDK.IActionPlugin => "action",
        _ => "utility"
    };
}
