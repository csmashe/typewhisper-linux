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
    private string? _editingActionId;
    private bool _isRefreshingProviders;

    public ObservableCollection<PromptAction> Actions { get; } = [];
    public ObservableCollection<ProviderOption> AvailableProviders { get; } = [];
    public ObservableCollection<ActionPluginOption> ActionPluginOptions { get; } = [];

    [ObservableProperty] private PromptAction? _selectedAction;
    [ObservableProperty] private bool _isCreatingNew;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editSystemPrompt = "";
    [ObservableProperty] private string _editIcon = "\u2728";
    [ObservableProperty] private string? _editProviderOverride;
    [ObservableProperty] private string? _editTargetActionPluginId;
    [ObservableProperty] private bool _showEditor;

    public bool HasSelectedAction => SelectedAction is not null || IsCreatingNew;
    public int ActionCount => Actions.Count;
    public int EnabledActionCount => Actions.Count(static action => action.IsEnabled);
    public string Summary => $"{ActionCount} prompts, {EnabledActionCount} enabled";
    public string PromptsHint => "AI prompts for the Prompt Palette. Select text + hotkey = AI processes the text.";
    public bool ShowProviderWarning => AvailableProviders.Count <= 1;
    public string ProviderWarningText => "Enable OpenAI or Groq in Extensions.";
    public bool ShowEmptyState => ActionCount == 0;
    public string EditorTitle => IsCreatingNew ? "New Prompt" : "Edit Prompt";
    public bool CanEditExistingAction => SelectedAction is not null;

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

    public ProviderOption? SelectedEditProvider
    {
        get => AvailableProviders.FirstOrDefault(option => option.Value == EditProviderOverride)
            ?? AvailableProviders.FirstOrDefault();
        set
        {
            if (_isRefreshingProviders)
                return;

            if (string.Equals(EditProviderOverride, value?.Value, StringComparison.Ordinal))
                return;

            EditProviderOverride = value?.Value;
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

        _prompts.ActionsChanged += () => Dispatcher.UIThread.Post(RefreshActions);
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshPluginOptions);
        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(DefaultLlmProvider)));

        RefreshPluginOptions();
        RefreshActions();
    }

    partial void OnSelectedActionChanged(PromptAction? value)
    {
        if (value is null)
        {
            if (!IsCreatingNew)
                ClearEditor();
            NotifyStateChanged();
            return;
        }

        IsCreatingNew = false;
        ShowEditor = true;
        _editingActionId = value.Id;
        EditName = value.Name;
        EditSystemPrompt = value.SystemPrompt;
        EditIcon = value.Icon;
        EditProviderOverride = value.ProviderOverride;
        EditTargetActionPluginId = value.TargetActionPluginId;
        NotifyStateChanged();
    }

    [RelayCommand]
    private void StartCreate()
    {
        IsCreatingNew = true;
        ShowEditor = true;
        SelectedAction = null;
        _editingActionId = null;
        EditName = "";
        EditSystemPrompt = "";
        EditIcon = "\u2728";
        EditProviderOverride = null;
        EditTargetActionPluginId = null;
        NotifyStateChanged();
    }

    [RelayCommand]
    private void SaveAction()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditSystemPrompt))
            return;

        if (IsCreatingNew)
        {
            var action = new PromptAction
            {
                Id = Guid.NewGuid().ToString(),
                Name = EditName.Trim(),
                SystemPrompt = EditSystemPrompt.Trim(),
                Icon = EditIcon,
                ProviderOverride = EditProviderOverride,
                TargetActionPluginId = EditTargetActionPluginId,
                IsEnabled = true,
                SortOrder = _prompts.Actions.Count
            };

            _prompts.AddAction(action);
            RefreshActions();
            SelectById(action.Id);
            return;
        }

        if (_editingActionId is null)
            return;

        var existing = _prompts.Actions.FirstOrDefault(action => action.Id == _editingActionId);
        if (existing is null)
            return;

        _prompts.UpdateAction(existing with
        {
            Name = EditName.Trim(),
            SystemPrompt = EditSystemPrompt.Trim(),
            Icon = EditIcon,
            ProviderOverride = EditProviderOverride,
            TargetActionPluginId = EditTargetActionPluginId
        });
        RefreshActions();
        SelectById(existing.Id);
    }

    [RelayCommand]
    private void EditAction(PromptAction? action)
    {
        if (action is null)
            return;

        SelectedAction = action;
    }

    [RelayCommand]
    private void DeleteSelectedAction()
    {
        if (SelectedAction is null || SelectedAction.IsPreset)
            return;

        _prompts.DeleteAction(SelectedAction.Id);
        RefreshActions();
        SelectedAction = null;
        ShowEditor = false;
    }

    [RelayCommand]
    private void ToggleEnabled(PromptAction? action)
    {
        if (action is null)
            return;

        _prompts.UpdateAction(action with { IsEnabled = !action.IsEnabled });
        RefreshActions();
    }

    [RelayCommand]
    private void MoveUp(PromptAction? action)
    {
        if (action is null)
            return;

        var orderedIds = _prompts.Actions.OrderBy(prompt => prompt.SortOrder).Select(prompt => prompt.Id).ToList();
        var index = orderedIds.IndexOf(action.Id);
        if (index <= 0)
            return;

        (orderedIds[index], orderedIds[index - 1]) = (orderedIds[index - 1], orderedIds[index]);
        _prompts.Reorder(orderedIds);
        RefreshActions();
    }

    [RelayCommand]
    private void MoveDown(PromptAction? action)
    {
        if (action is null)
            return;

        var orderedIds = _prompts.Actions.OrderBy(prompt => prompt.SortOrder).Select(prompt => prompt.Id).ToList();
        var index = orderedIds.IndexOf(action.Id);
        if (index < 0 || index >= orderedIds.Count - 1)
            return;

        (orderedIds[index], orderedIds[index + 1]) = (orderedIds[index + 1], orderedIds[index]);
        _prompts.Reorder(orderedIds);
        RefreshActions();
    }

    [RelayCommand]
    private void SeedPresets()
    {
        _prompts.SeedPresets();
        RefreshActions();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsCreatingNew = false;
        ShowEditor = false;
        SelectedAction = null;
        ClearEditor();
        NotifyStateChanged();
    }

    private void RefreshActions()
    {
        var selectedId = SelectedAction?.Id ?? _editingActionId;
        Actions.Clear();
        foreach (var action in _prompts.Actions.OrderBy(action => action.SortOrder))
            Actions.Add(action);

        if (selectedId is not null && ShowEditor)
        {
            SelectById(selectedId);
            return;
        }

        NotifyStateChanged();
    }

    private void RefreshPluginOptions()
    {
        var selectedProvider = EditProviderOverride;
        var selectedActionPlugin = EditTargetActionPluginId;

        _isRefreshingProviders = true;
        try
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

            EditProviderOverride = AvailableProviders.Any(option => option.Value == selectedProvider) ? selectedProvider : null;
        }
        finally
        {
            _isRefreshingProviders = false;
        }

        ActionPluginOptions.Clear();
        ActionPluginOptions.Add(new ActionPluginOption(null, "Insert text normally"));
        foreach (var actionPlugin in _pluginManager.ActionPlugins.OrderBy(plugin => plugin.ActionName))
            ActionPluginOptions.Add(new ActionPluginOption(actionPlugin.PluginId, actionPlugin.ActionName));

        EditTargetActionPluginId = ActionPluginOptions.Any(option => option.Value == selectedActionPlugin) ? selectedActionPlugin : null;
        OnPropertyChanged(nameof(SelectedEditProvider));
        OnPropertyChanged(nameof(ShowProviderWarning));
    }

    private void SelectById(string id)
    {
        var match = Actions.FirstOrDefault(action => action.Id == id);
        if (match is not null)
            SelectedAction = match;
        else
            NotifyStateChanged();
    }

    private void ClearEditor()
    {
        _editingActionId = null;
        EditName = "";
        EditSystemPrompt = "";
        EditIcon = "\u2728";
        EditProviderOverride = null;
        EditTargetActionPluginId = null;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasSelectedAction));
        OnPropertyChanged(nameof(ActionCount));
        OnPropertyChanged(nameof(EnabledActionCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ShowProviderWarning));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(CanEditExistingAction));
        OnPropertyChanged(nameof(SelectedEditProvider));
    }
}

public sealed record ProviderOption(string? Value, string Label);

public sealed record ActionPluginOption(string? Value, string Label);
