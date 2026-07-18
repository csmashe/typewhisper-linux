using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class PromptsSectionViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly IProfileService _profiles;
    private readonly IPromptActionService _prompts;
    private readonly ISettingsService _settings;
    private readonly HotkeyService _hotkeys;

    // Set while hydrating the spoken-command properties from saved settings so the
    // generated On<Property>Changed hooks don't persist the value straight back.
    private bool _hydratingCommandSettings;

    [ObservableProperty]
    private bool _commandModeEnabled;

    [ObservableProperty]
    private string _commandKeyphrase = AppSettings.DefaultCommandKeyphrase;

    [ObservableProperty]
    private string? _editHotkeyKey;

    [ObservableProperty]
    private string? _hotkeyValidationMessage;

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
        IProfileService profiles,
        HotkeyService hotkeys,
        PluginManager pluginManager,
        ISettingsService settings
    )
    {
        _prompts = prompts;
        _profiles = profiles;
        _hotkeys = hotkeys;
        _pluginManager = pluginManager;
        _settings = settings;

        _prompts.ActionsChanged += () => Dispatcher.UIThread.Post(RefreshActions);
        _pluginManager.PluginStateChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshPluginOptions);
        _settings.SettingsChanged += value =>
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(DefaultLlmProvider));
                OnPropertyChanged(nameof(SelectedSpokenCommandProvider));
                HydrateCommandSettings(value);
            });

        HydrateCommandSettings(_settings.Current);
        RefreshPluginOptions();
        RefreshActions();
    }

    public ObservableCollection<PromptAction> Actions { get; } = [];
    public ObservableCollection<ProviderOption> AvailableProviders { get; } = [];
    public ObservableCollection<ActionPluginOption> ActionPluginOptions { get; } = [];

    public bool HasSelectedAction => SelectedAction is not null || IsCreatingNew;
    public int ActionCount => Actions.Count;
    public int EnabledActionCount => Actions.Count(static action => action.IsEnabled);
    public string Summary =>
        Loc.Instance.GetString("Prompts.Summary", ActionCount, EnabledActionCount);

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string PromptsHint => Loc.Instance["Prompts.Hint"];

    public bool ShowProviderWarning => AvailableProviders.Count <= 1;
    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string ProviderWarningText => Loc.Instance["Prompts.ProviderWarning"];
    public bool ShowEmptyState => ActionCount == 0;
    public string EditorTitle =>
        IsCreatingNew ? Loc.Instance["Prompts.NewPrompt"] : Loc.Instance["Prompts.EditPrompt"];
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
    ///     Model used for spoken commands. Shares <see cref="AvailableProviders" />; the null "use
    ///     default" option persists as no override, deferring to <see cref="DefaultLlmProvider" />.
    /// </summary>
    public ProviderOption? SelectedSpokenCommandProvider
    {
        get =>
            AvailableProviders.FirstOrDefault(option =>
                option.Value == _settings.Current.SpokenCommandLlmProvider)
            ?? AvailableProviders.FirstOrDefault();
        set
        {
            if (_isRefreshingProviders)
            {
                return;
            }

            if (string.Equals(
                    _settings.Current.SpokenCommandLlmProvider,
                    value?.Value,
                    StringComparison.Ordinal))
            {
                return;
            }

            _settings.Save(_settings.Current with { SpokenCommandLlmProvider = value?.Value });
            OnPropertyChanged();
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

    private void HydrateCommandSettings(AppSettings settings)
    {
        _hydratingCommandSettings = true;
        try
        {
            CommandModeEnabled = settings.CommandModeEnabled;
            CommandKeyphrase = string.IsNullOrWhiteSpace(settings.CommandKeyphrase)
                ? AppSettings.DefaultCommandKeyphrase
                : settings.CommandKeyphrase;
        }
        finally
        {
            _hydratingCommandSettings = false;
        }
    }

    partial void OnCommandModeEnabledChanged(bool value)
    {
        if (_hydratingCommandSettings || _settings.Current.CommandModeEnabled == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { CommandModeEnabled = value });
    }

    partial void OnCommandKeyphraseChanged(string value)
    {
        if (_hydratingCommandSettings)
        {
            return;
        }

        // Guard empty: an unset keyphrase would match every dictation, so fall back to the
        // default. Normalizing re-enters this hook once with the trimmed value, which persists.
        var normalized = string.IsNullOrWhiteSpace(value)
            ? AppSettings.DefaultCommandKeyphrase
            : value.Trim();
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            CommandKeyphrase = normalized;
            return;
        }

        if (string.Equals(_settings.Current.CommandKeyphrase, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Save(_settings.Current with { CommandKeyphrase = normalized });
    }

    partial void OnSelectedActionChanged(PromptAction? value)
    {
        HotkeyValidationMessage = null;
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

    partial void OnEditHotkeyKeyChanged(string? value)
    {
        HotkeyValidationMessage = null;
    }

    [RelayCommand]
    private void StartCreate()
    {
        HotkeyValidationMessage = null;
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

        var hotkeyValidation = _hotkeys.ValidatePromptActionHotkeyCandidate(
            EditHotkeyKey,
            _editingActionId,
            _prompts.Actions,
            _profiles.Profiles
        );
        if (!hotkeyValidation.IsValid)
        {
            HotkeyValidationMessage = hotkeyValidation.Status switch
            {
                HotkeyCandidateValidationStatus.Malformed =>
                    Loc.Instance["Prompts.HotkeyMalformed"],
                _ => Loc.Instance["Prompts.HotkeyCollision"]
            };
            return;
        }

        EditHotkeyKey = hotkeyValidation.NormalizedHotkey;
        HotkeyValidationMessage = null;

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
                HotkeyKey = hotkeyValidation.NormalizedHotkey,
                IsManualOnly = EditIsManualOnly,
                IsEnabled = true,
                SortOrder = _prompts.Actions.Count
            };

            if (!TryMutate(() => _prompts.AddAction(action), "add a prompt action"))
            {
                return;
            }

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

        if (
            !TryMutate(
                () =>
                    _prompts.UpdateAction(
                        existing with
                        {
                            Name = EditName.Trim(),
                            SystemPrompt = EditSystemPrompt.Trim(),
                            Icon = EditIcon,
                            ProviderOverride = EditProviderOverride,
                            TargetActionPluginId = EditTargetActionPluginId,
                            HotkeyKey = hotkeyValidation.NormalizedHotkey,
                            IsManualOnly = EditIsManualOnly
                        }
                    ),
                "update a prompt action"
            )
        )
        {
            return;
        }

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

        if (!TryMutate(() => _prompts.DeleteAction(SelectedAction.Id), "delete a prompt action"))
        {
            return;
        }

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

        if (
            !TryMutate(
                () => _prompts.UpdateAction(action with { IsEnabled = !action.IsEnabled }),
                "toggle a prompt action"
            )
        )
        {
            return;
        }

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
        if (!TryMutate(() => _prompts.Reorder(orderedIds), "reorder prompt actions"))
        {
            return;
        }

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
        if (!TryMutate(() => _prompts.Reorder(orderedIds), "reorder prompt actions"))
        {
            return;
        }

        RefreshActions();
    }

    [RelayCommand]
    private void SeedPresets()
    {
        if (!TryMutate(_prompts.SeedPresets, "seed prompt presets"))
        {
            return;
        }

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

    private bool TryMutate(Action mutation, string operation)
    {
        try
        {
            mutation();
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PromptsSectionViewModel] Failed to {operation}: {ex}");
            RefreshActions();
            return false;
        }
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
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (
                var provider in _pluginManager.LlmProviders.Where(provider => provider.IsAvailable)
            )
            {
                // Use the provider's selection ID rather than mapping back to a loaded
                // plugin by reference — additional provider roles (e.g. OpenAI-compatible
                // profiles) are not themselves plugin instances, so a ReferenceEquals
                // lookup would skip them. For normal plugins the selection ID is the
                // plugin/manifest ID, so existing selections are unchanged.
                var selectionId = provider.GetLlmSelectionId();
                // ReSharper disable once LoopCanBeConvertedToQuery
                foreach (var model in provider.SupportedModels)
                {
                    resolvedOptions.Add(
                        new ProviderOption(
                            $"plugin:{selectionId}:{model.Id}",
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
        ActionPluginOptions.Add(
            new ActionPluginOption(null, Loc.Instance["Prompts.InsertTextNormally"])
        );
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
        OnPropertyChanged(nameof(SelectedSpokenCommandProvider));
        OnPropertyChanged(nameof(ShowProviderWarning));
    }

    private string DefaultProviderPlaceholderLabel(IReadOnlyList<ProviderOption> resolvedOptions)
    {
        var baseLabel = Loc.Instance["Prompts.UseDefaultProvider"];
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

        var fallbackModel = fallback.SupportedModels.Count > 0 ? fallback.SupportedModels[0] : null;
        var fallbackLabel = fallbackModel is null
            ? fallback.ProviderName
            : $"{fallback.ProviderName} / {fallbackModel.DisplayName}";
        return Loc.Instance.GetString("Prompts.UseDefaultProviderFallback", baseLabel, fallbackLabel);
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
        HotkeyValidationMessage = null;
        _editingActionId = null;
        EditName = "";
        EditSystemPrompt = "";
        EditIcon = "\u2728";
        EditProviderOverride = null;
        EditTargetActionPluginId = null;
        EditHotkeyKey = null;
        EditIsManualOnly = false;
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
