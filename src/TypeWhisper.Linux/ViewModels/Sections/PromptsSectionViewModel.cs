using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PromptsSectionViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly IPromptActionService _prompts;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private string? _editHotkeyKey;

    [ObservableProperty]
    private string _editIcon = "\u2728";

    private string? _editingActionId;

    [ObservableProperty]
    private bool _editIsManualOnly;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string? _editProviderOverride;

    [ObservableProperty]
    private string _editSystemPrompt = "";

    [ObservableProperty]
    private string? _editTargetActionPluginId;

    [ObservableProperty]
    private bool _isCreatingNew;

    // Prevents SelectedEditProvider's setter from persisting the provider
    // override while RefreshPluginOptions is rebuilding the provider list —
    // the setter fires when the ComboBox re-selects the current value.
    private bool _isRefreshingProviders;

    [ObservableProperty]
    private PromptAction? _selectedAction;

    [ObservableProperty]
    private bool _showEditor;

    public PromptsSectionViewModel(
        IPromptActionService prompts,
        PluginManager pluginManager,
        ISettingsService settings
    )
    {
        _prompts = prompts;
        _pluginManager = pluginManager;
        _settings = settings;

        _prompts.ActionsChanged += () => Dispatcher.UIThread.Post(RefreshActions);
        _pluginManager.PluginStateChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshPluginOptions);
        _settings.SettingsChanged += _ =>
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(DefaultLlmProvider)));

        RefreshPluginOptions();
        RefreshActions();
    }

    public ObservableCollection<PromptAction> Actions { get; } = [];
    public ObservableCollection<ProviderOption> AvailableProviders { get; } = [];
    public ObservableCollection<ActionPluginOption> ActionPluginOptions { get; } = [];

    public bool HasSelectedAction => SelectedAction is not null || IsCreatingNew;
    public int ActionCount => Actions.Count;
    public int EnabledActionCount => Actions.Count(static action => action.IsEnabled);
    public string Summary => $"{ActionCount} prompts, {EnabledActionCount} enabled";

    public string PromptsHint =>
        "AI prompts for the Prompt Palette. Select text + hotkey = AI processes the text.";

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
            if (
                string.Equals(_settings.Current.DefaultLlmProvider, value, StringComparison.Ordinal)
            )
            {
                return;
            }

            _settings.Save(_settings.Current with { DefaultLlmProvider = value });
            OnPropertyChanged();
        }
    }

    public ProviderOption? SelectedEditProvider
    {
        get =>
            AvailableProviders.FirstOrDefault(option => option.Value == EditProviderOverride)
            ?? AvailableProviders.FirstOrDefault();
        set
        {
            if (_isRefreshingProviders)
            {
                return;
            }

            if (string.Equals(EditProviderOverride, value?.Value, StringComparison.Ordinal))
            {
                return;
            }

            EditProviderOverride = value?.Value;
        }
    }

    /// <summary>
    ///     Re-polls providers for their current model list so new server-side models
    ///     appear without a manual "Validate". The dropdown rebuilds via
    ///     <c>PluginStateChanged</c> once the fetch lands; debounce lives in
    ///     <see cref="PluginManager" />.
    /// </summary>
    public Task RefreshProviderModelsAsync()
    {
        return _pluginManager.RefreshProviderModelsAsync();
    }

    partial void OnSelectedActionChanged(PromptAction? value)
    {
        if (value is null)
        {
            if (!IsCreatingNew)
            {
                ClearEditor();
            }

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
        EditHotkeyKey = value.HotkeyKey;
        EditIsManualOnly = value.IsManualOnly;
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
        EditHotkeyKey = null;
        EditIsManualOnly = false;
        NotifyStateChanged();
    }

    [RelayCommand]
    private void SaveAction()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditSystemPrompt))
        {
            return;
        }

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
                HotkeyKey = NormalizeOptionalString(EditHotkeyKey),
                IsManualOnly = EditIsManualOnly,
                IsEnabled = true,
                SortOrder = _prompts.Actions.Count
            };

            _prompts.AddAction(action);
            RefreshActions();
            SelectById(action.Id);
            return;
        }

        if (_editingActionId is null)
        {
            return;
        }

        var existing = _prompts.Actions.FirstOrDefault(action => action.Id == _editingActionId);
        if (existing is null)
        {
            return;
        }

        _prompts.UpdateAction(
            existing with
            {
                Name = EditName.Trim(),
                SystemPrompt = EditSystemPrompt.Trim(),
                Icon = EditIcon,
                ProviderOverride = EditProviderOverride,
                TargetActionPluginId = EditTargetActionPluginId,
                HotkeyKey = NormalizeOptionalString(EditHotkeyKey),
                IsManualOnly = EditIsManualOnly
            }
        );
        RefreshActions();
        SelectById(existing.Id);
    }

    [RelayCommand]
    private void EditAction(PromptAction? action)
    {
        if (action is null)
        {
            return;
        }

        SelectedAction = action;
    }

    [RelayCommand]
    private void DeleteSelectedAction()
    {
        if (SelectedAction is null || SelectedAction.IsPreset)
        {
            return;
        }

        _prompts.DeleteAction(SelectedAction.Id);
        RefreshActions();
        SelectedAction = null;
        ShowEditor = false;
    }

    [RelayCommand]
    private void ToggleEnabled(PromptAction? action)
    {
        if (action is null)
        {
            return;
        }

        _prompts.UpdateAction(action with { IsEnabled = !action.IsEnabled });
        RefreshActions();
    }

    [RelayCommand]
    private void MoveUp(PromptAction? action)
    {
        if (action is null)
        {
            return;
        }

        var orderedIds = _prompts
            .Actions.OrderBy(prompt => prompt.SortOrder)
            .Select(prompt => prompt.Id)
            .ToList();
        var index = orderedIds.IndexOf(action.Id);
        if (index <= 0)
        {
            return;
        }

        (orderedIds[index], orderedIds[index - 1]) = (orderedIds[index - 1], orderedIds[index]);
        _prompts.Reorder(orderedIds);
        RefreshActions();
    }

    [RelayCommand]
    private void MoveDown(PromptAction? action)
    {
        if (action is null)
        {
            return;
        }

        var orderedIds = _prompts
            .Actions.OrderBy(prompt => prompt.SortOrder)
            .Select(prompt => prompt.Id)
            .ToList();
        var index = orderedIds.IndexOf(action.Id);
        if (index < 0 || index >= orderedIds.Count - 1)
        {
            return;
        }

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
        {
            Actions.Add(action);
        }

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
            // Build the resolved list first to determine whether the "Use default
            // provider" placeholder needs a fallback suffix.
            var resolvedOptions = new List<ProviderOption>();
            foreach (
                var provider in _pluginManager.LlmProviders.Where(provider => provider.IsAvailable)
            )
            {
                var plugin = _pluginManager.AllPlugins.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Instance, provider)
                );
                if (plugin is null)
                {
                    continue;
                }

                foreach (var model in provider.SupportedModels)
                {
                    resolvedOptions.Add(
                        new ProviderOption(
                            $"plugin:{plugin.Manifest.Id}:{model.Id}",
                            $"{provider.ProviderName} / {model.DisplayName}"
                        )
                    );
                }
            }

            AvailableProviders.Clear();
            AvailableProviders.Add(new ProviderOption(null, DefaultProviderPlaceholderLabel(resolvedOptions)));
            foreach (var option in resolvedOptions)
            {
                AvailableProviders.Add(option);
            }

            EditProviderOverride = AvailableProviders.Any(option =>
                option.Value == selectedProvider
            )
                ? selectedProvider
                : null;
        }
        finally
        {
            _isRefreshingProviders = false;
        }

        ActionPluginOptions.Clear();
        ActionPluginOptions.Add(new ActionPluginOption(null, "Insert text normally"));
        foreach (
            var actionPlugin in _pluginManager.ActionPlugins.OrderBy(plugin => plugin.ActionName)
        )
        {
            ActionPluginOptions.Add(
                new ActionPluginOption(actionPlugin.PluginId, actionPlugin.ActionName)
            );
        }

        EditTargetActionPluginId = ActionPluginOptions.Any(option =>
            option.Value == selectedActionPlugin
        )
            ? selectedActionPlugin
            : null;
        OnPropertyChanged(nameof(SelectedEditProvider));
        OnPropertyChanged(nameof(ShowProviderWarning));
    }

    private string DefaultProviderPlaceholderLabel(IReadOnlyList<ProviderOption> resolvedOptions)
    {
        const string baseLabel = "Use default provider";
        var configured = _settings.Current.DefaultLlmProvider;
        var configuredResolves = !string.IsNullOrWhiteSpace(configured)
                                 && resolvedOptions.Any(option =>
                                     string.Equals(option.Value, configured, StringComparison.Ordinal));
        if (configuredResolves)
        {
            return baseLabel;
        }

        // Mirrors PromptProcessingService.ResolveProvider: first available LLM provider.
        var fallback = _pluginManager.LlmProviders.FirstOrDefault(provider => provider.IsAvailable);
        if (fallback is null)
        {
            return baseLabel;
        }

        var fallbackModel = fallback.SupportedModels.FirstOrDefault();
        var fallbackLabel = fallbackModel is null
            ? fallback.ProviderName
            : $"{fallback.ProviderName} / {fallbackModel.DisplayName}";
        return $"{baseLabel} ({fallbackLabel})";
    }

    private void SelectById(string id)
    {
        var match = Actions.FirstOrDefault(action => action.Id == id);
        if (match is not null)
        {
            SelectedAction = match;
        }
        else
        {
            NotifyStateChanged();
        }
    }

    private void ClearEditor()
    {
        _editingActionId = null;
        EditName = "";
        EditSystemPrompt = "";
        EditIcon = "\u2728";
        EditProviderOverride = null;
        EditTargetActionPluginId = null;
        EditHotkeyKey = null;
        EditIsManualOnly = false;
    }

    private static string? NormalizeOptionalString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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