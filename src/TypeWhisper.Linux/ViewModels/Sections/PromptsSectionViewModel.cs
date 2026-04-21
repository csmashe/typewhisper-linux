using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PromptsSectionViewModel : ObservableObject
{
    private readonly IPromptActionService _prompts;
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;

    public ObservableCollection<PromptAction> Actions { get; } = [];
    public ObservableCollection<ProviderOption> AvailableProviders { get; } = [];
    public ObservableCollection<ActionPluginOption> ActionPluginOptions { get; } = [];

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newSystemPrompt = "";
    [ObservableProperty] private string? _newProviderOverride;
    [ObservableProperty] private string? _newTargetActionPluginId;

    public string? DefaultLlmProvider
    {
        get => _settings.Current.DefaultLlmProvider;
        set
        {
            if (string.Equals(_settings.Current.DefaultLlmProvider, value, StringComparison.Ordinal))
                return;

            _settings.Save(_settings.Current with { DefaultLlmProvider = value });
            OnPropertyChanged();
        }
    }

    public PromptsSectionViewModel(
        IPromptActionService prompts,
        PluginManager pluginManager,
        ISettingsService settings)
    {
        _prompts = prompts;
        _pluginManager = pluginManager;
        _settings = settings;
        _prompts.ActionsChanged += () => Dispatcher.UIThread.Post(Refresh);
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshPluginOptions);
        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(DefaultLlmProvider)));
        RefreshPluginOptions();
        Refresh();
    }

    private void Refresh()
    {
        Actions.Clear();
        foreach (var a in _prompts.Actions.OrderBy(a => a.SortOrder).ThenBy(a => a.Name))
            Actions.Add(a);
    }

    private void RefreshPluginOptions()
    {
        AvailableProviders.Clear();
        AvailableProviders.Add(new ProviderOption(null, "Use default provider"));

        foreach (var provider in _pluginManager.LlmProviders.Where(provider => provider.IsAvailable))
        {
            var plugin = _pluginManager.AllPlugins.FirstOrDefault(candidate => ReferenceEquals(candidate.Instance, provider));
            if (plugin is null)
                continue;

            foreach (var model in provider.SupportedModels)
            {
                AvailableProviders.Add(new ProviderOption(
                    $"plugin:{plugin.Manifest.Id}:{model.Id}",
                    $"{provider.ProviderName} / {model.DisplayName}"));
            }
        }

        ActionPluginOptions.Clear();
        ActionPluginOptions.Add(new ActionPluginOption(null, "Insert text normally"));
        foreach (var actionPlugin in _pluginManager.ActionPlugins.OrderBy(plugin => plugin.ActionName))
            ActionPluginOptions.Add(new ActionPluginOption(actionPlugin.PluginId, actionPlugin.ActionName));

        if (!AvailableProviders.Any(option => option.Value == NewProviderOverride))
            NewProviderOverride = null;
        if (!ActionPluginOptions.Any(option => option.Value == NewTargetActionPluginId))
            NewTargetActionPluginId = null;
    }

    [RelayCommand]
    private void AddPrompt()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewSystemPrompt)) return;
        _prompts.AddAction(new PromptAction
        {
            Id = Guid.NewGuid().ToString(),
            Name = NewName.Trim(),
            SystemPrompt = NewSystemPrompt.Trim(),
            IsEnabled = true,
            IsPreset = false,
            SortOrder = Actions.Count,
            ProviderOverride = NewProviderOverride,
            TargetActionPluginId = NewTargetActionPluginId,
        });
        NewName = "";
        NewSystemPrompt = "";
        NewProviderOverride = null;
        NewTargetActionPluginId = null;
    }

    [RelayCommand]
    private void Delete(PromptAction p) => _prompts.DeleteAction(p.Id);

    [RelayCommand]
    private void ToggleEnabled(PromptAction p) => _prompts.UpdateAction(p with { IsEnabled = !p.IsEnabled });

    [RelayCommand]
    private void SeedPresets() => _prompts.SeedPresets();
}

public sealed record ProviderOption(string? Value, string Label);

public sealed record ActionPluginOption(string? Value, string Label);
