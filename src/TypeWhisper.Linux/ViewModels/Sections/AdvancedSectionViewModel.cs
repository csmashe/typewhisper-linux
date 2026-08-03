using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AdvancedSectionViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;
    private readonly Action<Action> _post;
    private readonly ISettingsService _settings;

    // Set while Refresh applies persisted state so the generated On<Property>Changed hooks don't
    // write it straight back: their equality guards compare against _settings.Current, which a
    // queued refresh has already fallen behind, so a stale value would overwrite the newer commit.
    private bool _hydratingFromSettings;
    private readonly SpeechFeedbackService _speechFeedback;

    [ObservableProperty]
    private bool _captureLlmProvenance;

    [ObservableProperty]
    private bool _memoryEnabled;

    [ObservableProperty]
    private bool _saveToHistoryEnabled;

    [ObservableProperty]
    private AutoUnloadOption? _selectedAutoUnloadOption;

    [ObservableProperty]
    private HistoryRetentionOption? _selectedHistoryRetention;

    [ObservableProperty]
    private string _selectedSpokenFeedbackProviderId = AppSettings.DefaultSpokenFeedbackProviderId;

    [ObservableProperty]
    private string? _selectedSpokenFeedbackVoiceId;

    [ObservableProperty]
    private bool _spokenFeedbackEnabled;

    // post marshals refreshes onto the UI thread; it is injected rather than calling
    // Dispatcher.UIThread directly because that dispatcher binds to whichever thread touches it
    // first and nothing pumps it under the test runner, so tests pass a synchronous one.
    public AdvancedSectionViewModel(
        ISettingsService settings,
        SpeechFeedbackService speechFeedback,
        PluginManager pluginManager,
        Action<Action>? post = null
    )
    {
        _settings = settings;
        _speechFeedback = speechFeedback;
        _pluginManager = pluginManager;
        _post = post ?? PostToUiThread;
        Refresh(settings.Current);
        RefreshSpokenFeedbackProviders();

        // Subscribe only once hydration has run. RefreshSpokenFeedbackProviders falls back to the
        // default provider when the selected one is absent, and that write is not under the
        // hydration guard — firing it against an un-hydrated selection would persist the default
        // over the user's saved provider. Every callback goes through _post: plugin and provider
        // notifications can arrive on background threads, and they also touch the properties the
        // hydration flag guards, so they must be serialized with Refresh rather than race it.
        _speechFeedback.ProvidersChanged += (_, _) => _post(RefreshSpokenFeedbackProviders);
        _settings.SettingsChanged += OnSettingsChanged;
        _pluginManager.PluginStateChanged += (_, _) => _post(() =>
        {
            OnPropertyChanged(nameof(CanUseMemory));
            OnPropertyChanged(nameof(ShowMemoryUnavailableReason));
            OnPropertyChanged(nameof(MemoryHint));
            OnPropertyChanged(nameof(CanUseSpokenFeedback));
            OnPropertyChanged(nameof(ShowSpokenFeedbackUnavailableReason));
            OnPropertyChanged(nameof(SpokenFeedbackHint));
            RefreshSpokenFeedbackProviders();
            if (!CanUseMemory && MemoryEnabled)
            {
                MemoryEnabled = false;
            }
        });
    }

    public ObservableCollection<TtsProviderOption> SpokenFeedbackProviders { get; } = [];
    public ObservableCollection<TtsVoiceOption> SpokenFeedbackVoices { get; } = [];

    public TtsProviderOption? SelectedSpokenFeedbackProviderOption
    {
        get =>
            SpokenFeedbackProviders.FirstOrDefault(provider =>
                string.Equals(
                    provider.Id,
                    SelectedSpokenFeedbackProviderId,
                    StringComparison.Ordinal
                )
            );
        set
        {
            if (
                value is null
                || string.Equals(
                    value.Id,
                    SelectedSpokenFeedbackProviderId,
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            SelectedSpokenFeedbackProviderId = value.Id;
            OnPropertyChanged();
        }
    }

    public TtsVoiceOption? SelectedSpokenFeedbackVoiceOption
    {
        get =>
            SpokenFeedbackVoices.FirstOrDefault(voice =>
                string.Equals(voice.Id, SelectedSpokenFeedbackVoiceId, StringComparison.Ordinal)
            );
        set
        {
            if (
                value is null
                || string.Equals(value.Id, SelectedSpokenFeedbackVoiceId, StringComparison.Ordinal)
            )
            {
                return;
            }

            SelectedSpokenFeedbackVoiceId = value.Id;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<AutoUnloadOption> AutoUnloadOptions { get; } =
    [
        new(0, Loc.Instance["Advanced.AutoUnloadNever"]),
        new(30, Loc.Instance["Advanced.AutoUnload30Seconds"]),
        new(60, Loc.Instance["Advanced.AutoUnload1Minute"]),
        new(300, Loc.Instance["Advanced.AutoUnload5Minutes"]),
        new(900, Loc.Instance["Advanced.AutoUnload15Minutes"])
    ];

    public IReadOnlyList<HistoryRetentionOption> HistoryRetentionOptions { get; } =
    [
        new(HistoryRetentionMode.Duration, 24 * 60, Loc.Instance["Advanced.Retention1Day"]),
        new(HistoryRetentionMode.Duration, 7 * 24 * 60, Loc.Instance["Advanced.Retention7Days"]),
        new(HistoryRetentionMode.Duration, 30 * 24 * 60, Loc.Instance["Advanced.Retention30Days"]),
        new(HistoryRetentionMode.Duration, 90 * 24 * 60, Loc.Instance["Advanced.Retention90Days"]),
        new(HistoryRetentionMode.Forever, null, Loc.Instance["Advanced.RetentionForever"]),
        new(HistoryRetentionMode.UntilAppCloses, null, Loc.Instance["Advanced.RetentionUntilAppCloses"])
    ];

    public bool CanUseSpokenFeedback => _speechFeedback.IsAvailable;
    public bool ShowSpokenFeedbackUnavailableReason => !CanUseSpokenFeedback;

    private static string SpokenFeedbackUnavailableReason =>
        Loc.Instance["Advanced.SpokenFeedbackUnavailable"];

    public string SpokenFeedbackHint =>
        CanUseSpokenFeedback
            ? Loc.Instance.GetString(
                "Advanced.SpokenFeedbackHint",
                _speechFeedback.BackendName
            )
            : SpokenFeedbackUnavailableReason;

    public bool CanUseMemory =>
        _pluginManager.GetPlugins<IMemoryStoragePlugin>().Any()
        && _pluginManager.LlmProviders.Any(provider => provider.IsAvailable);

    public bool ShowMemoryUnavailableReason => !CanUseMemory;

    private static string MemoryUnavailableReason =>
        Loc.Instance["Advanced.MemoryUnavailable"];

    public string MemoryHint =>
        CanUseMemory
            ? Loc.Instance["Advanced.MemoryHint"]
            : MemoryUnavailableReason;

    // Saves happen on whichever thread called them — the dictation path and the model-storage
    // migration both save off the UI thread — and Refresh writes bound properties.
    private void OnSettingsChanged(AppSettings settings)
    {
        // Read Current when the post runs rather than capturing the payload, so queued
        // refreshes coalesce onto the newest commit instead of replaying superseded ones.
        _post(() => Refresh(_settings.Current));
    }

    private static void PostToUiThread(Action action)
    {
        // Inline when already on the UI thread, so a save from the UI keeps refreshing
        // synchronously rather than deferring to the next dispatcher turn.
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void Refresh(AppSettings settings)
    {
        // Restore rather than clear: a nested Refresh must not un-guard the remainder
        // of the outer one, which would let it write its older snapshot back.
        var wasHydrating = _hydratingFromSettings;
        _hydratingFromSettings = true;
        try
        {
            ApplySettings(settings);
        }
        finally
        {
            _hydratingFromSettings = wasHydrating;
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        MemoryEnabled = settings.MemoryEnabled && CanUseMemory;
        SpokenFeedbackEnabled = settings.SpokenFeedbackEnabled && CanUseSpokenFeedback;
        SaveToHistoryEnabled = settings.SaveToHistoryEnabled;
        CaptureLlmProvenance = settings.CaptureLlmProvenance;
        SelectedSpokenFeedbackProviderId = string.IsNullOrWhiteSpace(
            settings.SpokenFeedbackProviderId
        )
            ? AppSettings.DefaultSpokenFeedbackProviderId
            : settings.SpokenFeedbackProviderId;
        SelectedSpokenFeedbackVoiceId =
            settings.SpokenFeedbackVoiceId ?? SpeechFeedbackService.DefaultVoiceOptionId;
        SelectedAutoUnloadOption =
            AutoUnloadOptions.FirstOrDefault(option =>
                option.Seconds == settings.ModelAutoUnloadSeconds
            ) ?? AutoUnloadOptions[0];
        SelectedHistoryRetention = MatchRetention(
            settings.HistoryRetentionMode,
            settings.HistoryRetentionMinutes
        );
    }

    partial void OnMemoryEnabledChanged(bool value)
    {
        if (_hydratingFromSettings || _settings.Current.MemoryEnabled == value)
        {
            return;
        }

        if (value && !CanUseMemory)
        {
            MemoryEnabled = false;
            return;
        }

        _settings.Save(_settings.Current with { MemoryEnabled = value });
    }

    partial void OnSelectedAutoUnloadOptionChanged(AutoUnloadOption? value)
    {
        if (_hydratingFromSettings || value is null || _settings.Current.ModelAutoUnloadSeconds == value.Seconds)
        {
            return;
        }

        _settings.Save(_settings.Current with { ModelAutoUnloadSeconds = value.Seconds });
    }

    partial void OnSpokenFeedbackEnabledChanged(bool value)
    {
        if (_hydratingFromSettings || _settings.Current.SpokenFeedbackEnabled == value)
        {
            return;
        }

        if (value && !CanUseSpokenFeedback)
        {
            SpokenFeedbackEnabled = false;
            return;
        }

        _settings.Save(_settings.Current with { SpokenFeedbackEnabled = value });
    }

    partial void OnSelectedSpokenFeedbackProviderIdChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            value = AppSettings.DefaultSpokenFeedbackProviderId;
        }

        RefreshSpokenFeedbackVoices();

        if (_hydratingFromSettings || _settings.Current.SpokenFeedbackProviderId == value)
        {
            return;
        }

        var selectedVoiceId = SpeechFeedbackService.IsDefaultVoiceOptionId(
            SelectedSpokenFeedbackVoiceId
        )
            ? null
            : SelectedSpokenFeedbackVoiceId;
        _settings.Save(
            _settings.Current with { SpokenFeedbackProviderId = value, SpokenFeedbackVoiceId = selectedVoiceId }
        );
        OnPropertyChanged(nameof(SelectedSpokenFeedbackProviderOption));
    }

    partial void OnSelectedSpokenFeedbackVoiceIdChanged(string? value)
    {
        _speechFeedback.SelectVoice(SelectedSpokenFeedbackProviderId, value);
        var normalized = SpeechFeedbackService.IsDefaultVoiceOptionId(value) ? null : value;
        if (_hydratingFromSettings || _settings.Current.SpokenFeedbackVoiceId == normalized)
        {
            return;
        }

        _settings.Save(_settings.Current with { SpokenFeedbackVoiceId = normalized });
        OnPropertyChanged(nameof(SelectedSpokenFeedbackVoiceOption));
    }

    partial void OnSaveToHistoryEnabledChanged(bool value)
    {
        if (_hydratingFromSettings || _settings.Current.SaveToHistoryEnabled == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { SaveToHistoryEnabled = value });
    }

    partial void OnCaptureLlmProvenanceChanged(bool value)
    {
        if (_hydratingFromSettings || _settings.Current.CaptureLlmProvenance == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { CaptureLlmProvenance = value });
    }

    partial void OnSelectedHistoryRetentionChanged(HistoryRetentionOption? value)
    {
        if (_hydratingFromSettings || value is null)
        {
            return;
        }

        if (
            _settings.Current.HistoryRetentionMode == value.Mode
            && (
                value.Mode != HistoryRetentionMode.Duration
                || _settings.Current.HistoryRetentionMinutes == value.Minutes
            )
        )
        {
            return;
        }

        _settings.Save(
            _settings.Current with
            {
                HistoryRetentionMode = value.Mode,
                HistoryRetentionMinutes =
                value.Minutes ?? _settings.Current.HistoryRetentionMinutes
            }
        );
    }

    // First try exact match; if the stored minutes value no longer matches any
    // option (e.g. a custom value from a future version), fall back to the
    // app default, then to the first option as a last resort.
    private HistoryRetentionOption MatchRetention(HistoryRetentionMode mode, int minutes)
    {
        return HistoryRetentionOptions.FirstOrDefault(option =>
                   option.Mode == mode
                   && (mode != HistoryRetentionMode.Duration || option.Minutes == minutes)
               )
               ?? HistoryRetentionOptions.FirstOrDefault(option =>
                   option.Mode == AppSettings.Default.HistoryRetentionMode
                   && option.Minutes == AppSettings.Default.HistoryRetentionMinutes
               )
               ?? HistoryRetentionOptions[0];
    }

    private void RefreshSpokenFeedbackProviders()
    {
        ReplaceCollection(SpokenFeedbackProviders, _speechFeedback.AvailableProviders);
        if (
            SpokenFeedbackProviders.All(provider => provider.Id != SelectedSpokenFeedbackProviderId)
        )
        {
            SelectedSpokenFeedbackProviderId = AppSettings.DefaultSpokenFeedbackProviderId;
        }

        RefreshSpokenFeedbackVoices();
        OnPropertyChanged(nameof(SelectedSpokenFeedbackProviderOption));
    }

    private void RefreshSpokenFeedbackVoices()
    {
        ReplaceCollection(
            SpokenFeedbackVoices,
            _speechFeedback.GetVoiceOptions(SelectedSpokenFeedbackProviderId)
        );
        var selected = _speechFeedback.GetSelectedVoiceId(SelectedSpokenFeedbackProviderId);
        SelectedSpokenFeedbackVoiceId = SpokenFeedbackVoices.Any(voice => voice.Id == selected)
            ? selected
            : SpeechFeedbackService.DefaultVoiceOptionId;
        OnPropertyChanged(nameof(SelectedSpokenFeedbackVoiceOption));
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var snapshot = items.ToList();
        if (target.SequenceEqual(snapshot))
        {
            return;
        }

        target.Clear();
        foreach (var item in snapshot)
        {
            target.Add(item);
        }
    }
}

public sealed record AutoUnloadOption(int Seconds, string DisplayName);

public sealed record HistoryRetentionOption(
    HistoryRetentionMode Mode,
    int? Minutes,
    string DisplayName
);