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
    private readonly IErrorLogService? _errorLog;
    private readonly PluginManager _pluginManager;
    private readonly IProfileService _profiles;
    private readonly IPromptActionService _prompts;
    private readonly ISettingsService _settings;
    private readonly HotkeyService _hotkeys;

    [ObservableProperty]
    private string _errorText = "";

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

    // Placeholder added for the editor's current unresolvable override, so it can be dropped again
    // when the editor moves on. Null while the list is whatever RefreshPluginOptions last built.
    private ProviderOption? _synthesizedProviderOption;

    [ObservableProperty]
    private PromptAction? _selectedAction;

    [ObservableProperty]
    private bool _showEditor;

    public PromptsSectionViewModel(
        IPromptActionService prompts,
        IProfileService profiles,
        HotkeyService hotkeys,
        PluginManager pluginManager,
        ISettingsService settings,
        IErrorLogService? errorLog = null
    )
    {
        _prompts = prompts;
        _profiles = profiles;
        _hotkeys = hotkeys;
        _pluginManager = pluginManager;
        _settings = settings;
        _errorLog = errorLog;

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

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool ShowProviderWarning =>
        !AvailableProviders.Any(option => option.Value is not null && !option.IsUnavailable);
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

            _settings.Update(current => current with { DefaultLlmProvider = value });
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

            _settings.Update(current => current with { SpokenCommandLlmProvider = value?.Value });
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

        _settings.Update(current => current with { CommandModeEnabled = value });
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

        _settings.Update(current => current with { CommandKeyphrase = normalized });
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

    // Hydrating an action whose override no longer resolves (uninstalled plugin, signed-out CLI)
    // must not leave the picker showing "Use default provider": the action still carries the
    // override and would fail on it. The option list only rebuilds on plugin state changes, so
    // add the placeholder here too — and drop the one synthesized for the previous action, which
    // otherwise stays listed and selectable for every action visited since the last refresh.
    partial void OnEditProviderOverrideChanged(string? value)
    {
        if (_isRefreshingProviders)
        {
            return;
        }

        if (_synthesizedProviderOption is not null
            && !string.Equals(_synthesizedProviderOption.Value, value, StringComparison.Ordinal))
        {
            AvailableProviders.Remove(_synthesizedProviderOption);
            _synthesizedProviderOption = null;
        }

        if (!string.IsNullOrWhiteSpace(value)
            && AvailableProviders.All(option =>
                !string.Equals(option.Value, value, StringComparison.Ordinal)))
        {
            _synthesizedProviderOption = MissingSelectionOption(value);
            AvailableProviders.Add(_synthesizedProviderOption);
        }

        OnPropertyChanged(nameof(SelectedEditProvider));
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

        var existing = _editingActionId is null
            ? null
            : _prompts.Actions.FirstOrDefault(action => action.Id == _editingActionId);
        if (!IsCreatingNew && existing is null)
        {
            return;
        }

        // Disabled outcomes keep the draft chord unvalidated; the enable gate
        // (ToggleEnabled here) validates it before it can ever bind.
        string? hotkeyKey;
        if (!IsCreatingNew && existing is { IsEnabled: false })
        {
            hotkeyKey = string.IsNullOrWhiteSpace(EditHotkeyKey) ? null : EditHotkeyKey;
        }
        else
        {
            var hotkeyValidation = _hotkeys.ValidatePromptActionHotkeyCandidate(
                EditHotkeyKey,
                _editingActionId,
                _prompts.Actions,
                _profiles.Profiles
            );
            if (!hotkeyValidation.IsValid)
            {
                HotkeyValidationMessage = GetHotkeyValidationMessage(hotkeyValidation.Status);
                return;
            }

            hotkeyKey = hotkeyValidation.NormalizedHotkey;
        }

        EditHotkeyKey = hotkeyKey;
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
                HotkeyKey = hotkeyKey,
                IsManualOnly = EditIsManualOnly,
                IsEnabled = true,
                SortOrder = _prompts.Actions.Count,
            };

            if (!TryMutate(() => _prompts.AddAction(action), "add a prompt action"))
            {
                return;
            }

            RefreshActions();
            SelectById(action.Id);
            return;
        }

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
                            HotkeyKey = hotkeyKey,
                            IsManualOnly = EditIsManualOnly,
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

        if (!action.IsEnabled)
        {
            var hotkeyValidation = _hotkeys.ValidatePromptActionHotkeyCandidate(
                action.HotkeyKey,
                action.Id,
                _prompts.Actions,
                _profiles.Profiles
            );
            if (!hotkeyValidation.IsValid)
            {
                SelectById(action.Id);
                HotkeyValidationMessage = GetHotkeyValidationMessage(hotkeyValidation.Status);
                return;
            }
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

    private static string GetHotkeyValidationMessage(HotkeyCandidateValidationStatus status)
    {
        return status switch
        {
            HotkeyCandidateValidationStatus.Malformed =>
                Loc.Instance["Prompts.HotkeyMalformed"],
            _ => Loc.Instance["Prompts.HotkeyCollision"],
        };
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
            ErrorText = "";
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PromptsSectionViewModel] Failed to {operation}: {ex}");
            _errorLog?.AddEntry($"Could not {operation}: {ex.Message}", ErrorCategory.Prompt);
            ErrorText = Loc.Instance.GetString("Prompts.SaveFailed", ex.Message);
            RefreshActions();
            return false;
        }
    }

    partial void OnErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
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
            ProviderOption? firstAvailableOption = null;
            foreach (var provider in _pluginManager.LlmProviders)
            {
                // Use the provider's selection ID rather than mapping back to a loaded
                // plugin by reference — additional provider roles (e.g. OpenAI-compatible
                // profiles) are not themselves plugin instances, so a ReferenceEquals
                // lookup would skip them. For normal plugins the selection ID is the
                // plugin/manifest ID, so existing selections are unchanged.
                var selectionId = provider.GetLlmSelectionId();
                foreach (var model in provider.SupportedModels)
                {
                    // A provider that is installed but not ready (signed out, no key) stays
                    // listed and labelled — dropping it would silently reset a selection the
                    // user made, and PromptProcessingService now reports it by name instead
                    // of falling through to another provider.
                    var label = $"{provider.ProviderName} / {model.DisplayName}";
                    var option = new ProviderOption(
                        $"plugin:{selectionId}:{model.Id}",
                        provider.IsAvailable
                            ? label
                            : Loc.Instance.GetString("Prompts.ProviderUnavailableFormat", label),
                        !provider.IsAvailable
                    );
                    resolvedOptions.Add(option);
                    if (provider.IsAvailable)
                    {
                        firstAvailableOption ??= option;
                    }
                }
            }

            AddMissingSelectionOption(resolvedOptions, _settings.Current.DefaultLlmProvider);
            AddMissingSelectionOption(resolvedOptions, selectedProvider);
            AddMissingSelectionOption(resolvedOptions, _settings.Current.SpokenCommandLlmProvider);

            // The rebuilt list carries every saved selection again, so nothing synthesized before it
            // is still owned by the editor.
            _synthesizedProviderOption = null;
            AvailableProviders.Clear();
            AvailableProviders.Add(
                new ProviderOption(
                    null,
                    DefaultProviderPlaceholderLabel(resolvedOptions, firstAvailableOption)
                )
            );
            foreach (var option in resolvedOptions)
            {
                AvailableProviders.Add(option);
            }
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

    // Keeps a saved selection selectable after the provider behind it disappears, so a refresh
    // never silently rewrites the user's choice.
    private static void AddMissingSelectionOption(List<ProviderOption> options, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
        {
            return;
        }

        options.Add(MissingSelectionOption(value));
    }

    private static ProviderOption MissingSelectionOption(string value)
    {
        return new ProviderOption(
            value,
            Loc.Instance.GetString(
                "Prompts.ProviderUnavailableFormat",
                PromptProcessingService.DescribeSelection(value)
            ),
            true
        );
    }

    private string DefaultProviderPlaceholderLabel(
        IReadOnlyList<ProviderOption> resolvedOptions,
        ProviderOption? firstAvailableOption
    )
    {
        var baseLabel = Loc.Instance["Prompts.UseDefaultProvider"];
        var configured = _settings.Current.DefaultLlmProvider;
        var configuredOption = string.IsNullOrWhiteSpace(configured)
            ? null
            : resolvedOptions.FirstOrDefault(option =>
                string.Equals(option.Value, configured, StringComparison.Ordinal));
        if (configuredOption is not null)
        {
            // A configured default that can't serve the request is named, not hidden — the
            // placeholder is the only place the default provider is shown.
            return configuredOption.IsUnavailable
                ? Loc.Instance.GetString(
                    "Prompts.UseDefaultProviderFallback",
                    baseLabel,
                    configuredOption.Label
                )
                : baseLabel;
        }

        // Mirrors PromptProcessingService.ResolveProvider: first available LLM provider.
        return firstAvailableOption is null
            ? baseLabel
            : Loc.Instance.GetString(
                "Prompts.UseDefaultProviderFallback",
                baseLabel,
                firstAvailableOption.Label
            );
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

/// <summary>
///     One entry of the provider dropdown. IsUnavailable marks a provider that is listed but
///     cannot serve a request (signed out, missing key, no longer installed).
/// </summary>
public sealed record ProviderOption(string? Value, string Label, bool IsUnavailable = false);

public sealed record ActionPluginOption(string? Value, string Label);
