// ReSharper disable ArrangeObjectCreationWhenTypeNotEvident -- target-typed `new(...)` inside collection
// expressions and record construction is the prevailing style across this codebase.
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
    private readonly SpeechFeedbackService _speechFeedback;
    private bool _configuredMemoryEnabled;
    private bool _configuredSpokenFeedbackEnabled;
    private string _configuredSpokenFeedbackProviderId =
        AppSettings.DefaultSpokenFeedbackProviderId;
    private string? _configuredSpokenFeedbackVoiceId =
        SpeechFeedbackService.DefaultVoiceOptionId;
    private bool _isProgrammaticRefresh;
    private long _refreshGeneration;

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

    public AdvancedSectionViewModel(
        ISettingsService settings,
        SpeechFeedbackService speechFeedback,
        PluginManager pluginManager
    )
        : this(
            settings,
            speechFeedback,
            pluginManager,
            action => Dispatcher.UIThread.Post(action)
        )
    {
    }

    internal AdvancedSectionViewModel(
        ISettingsService settings,
        SpeechFeedbackService speechFeedback,
        PluginManager pluginManager,
        Action<Action> post
    )
    {
        _settings = settings;
        _speechFeedback = speechFeedback;
        _pluginManager = pluginManager;
        _post = post;
        RefreshSpokenFeedbackProviders();
        Refresh(settings.Current);

        // Subscribe only once hydration has run: RefreshSpokenFeedbackProviders falls back to the
        // default provider when the selected one is absent, and firing that against an un-hydrated
        // selection would persist the default over the user's saved provider.
        _speechFeedback.ProvidersChanged += (_, _) => PostPluginStateRefresh();

        // SettingsChanged can fire off the UI thread (HTTP API, model manager), and
        // Refresh mutates the provider/voice collections.
        _settings.SettingsChanged += changed => _post(() => Refresh(changed));
        _pluginManager.PluginStateChanged += (_, _) => PostPluginStateRefresh();
        Loc.Instance.LanguageChanged += OnInterfaceLanguageChanged;
    }

    private void PostPluginStateRefresh()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        _post(() =>
        {
            if (generation != Interlocked.Read(ref _refreshGeneration))
            {
                return;
            }

            RunProgrammaticRefresh(() =>
            {
                OnPropertyChanged(nameof(CanUseMemory));
                OnPropertyChanged(nameof(ShowMemoryUnavailableReason));
                OnPropertyChanged(nameof(MemoryHint));
                OnPropertyChanged(nameof(CanUseSpokenFeedback));
                OnPropertyChanged(nameof(ShowSpokenFeedbackUnavailableReason));
                OnPropertyChanged(nameof(SpokenFeedbackHint));
                RefreshSpokenFeedbackProviders();
                MemoryEnabled = _configuredMemoryEnabled && CanUseMemory;
                SpokenFeedbackEnabled = _configuredSpokenFeedbackEnabled && CanUseSpokenFeedback;
            });
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

    public IReadOnlyList<AutoUnloadOption> AutoUnloadOptions { get; private set; } =
        CreateAutoUnloadOptions();

    public IReadOnlyList<HistoryRetentionOption> HistoryRetentionOptions { get; private set; } =
        CreateHistoryRetentionOptions();

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

    private void Refresh(AppSettings settings)
    {
        _configuredMemoryEnabled = settings.MemoryEnabled;
        _configuredSpokenFeedbackEnabled = settings.SpokenFeedbackEnabled;
        _configuredSpokenFeedbackProviderId = NormalizeProviderId(
            settings.SpokenFeedbackProviderId
        );
        _configuredSpokenFeedbackVoiceId =
            settings.SpokenFeedbackVoiceId ?? SpeechFeedbackService.DefaultVoiceOptionId;

        RunProgrammaticRefresh(() =>
        {
            MemoryEnabled = _configuredMemoryEnabled && CanUseMemory;
            SpokenFeedbackEnabled = _configuredSpokenFeedbackEnabled && CanUseSpokenFeedback;
            SaveToHistoryEnabled = settings.SaveToHistoryEnabled;
            CaptureLlmProvenance = settings.CaptureLlmProvenance;
            ApplyEffectiveSpokenFeedbackPreference();
            SelectedAutoUnloadOption =
                AutoUnloadOptions.FirstOrDefault(option =>
                    option.Seconds == settings.ModelAutoUnloadSeconds
                ) ?? AutoUnloadOptions[0];
            SelectedHistoryRetention = MatchRetention(
                settings.HistoryRetentionMode,
                settings.HistoryRetentionMinutes
            );
        });
    }

    partial void OnMemoryEnabledChanged(bool value)
    {
        if (_isProgrammaticRefresh)
        {
            return;
        }

        if (_settings.Current.MemoryEnabled == value)
        {
            _configuredMemoryEnabled = value;
            return;
        }

        if (value && !CanUseMemory)
        {
            RunProgrammaticRefresh(() => MemoryEnabled = false);
            return;
        }

        _configuredMemoryEnabled = value;
        _settings.Update(current => current with { MemoryEnabled = value });
    }

    partial void OnSelectedAutoUnloadOptionChanged(AutoUnloadOption? value)
    {
        if (
            _isProgrammaticRefresh
            || value is null
            || _settings.Current.ModelAutoUnloadSeconds == value.Seconds
        )
        {
            return;
        }

        _settings.Update(current => current with { ModelAutoUnloadSeconds = value.Seconds });
    }

    partial void OnSpokenFeedbackEnabledChanged(bool value)
    {
        if (_isProgrammaticRefresh)
        {
            return;
        }

        if (_settings.Current.SpokenFeedbackEnabled == value)
        {
            _configuredSpokenFeedbackEnabled = value;
            return;
        }

        if (value && !CanUseSpokenFeedback)
        {
            RunProgrammaticRefresh(() => SpokenFeedbackEnabled = false);
            return;
        }

        _configuredSpokenFeedbackEnabled = value;
        _settings.Update(current => current with { SpokenFeedbackEnabled = value });
    }

    partial void OnSelectedSpokenFeedbackProviderIdChanged(string value)
    {
        RefreshSpokenFeedbackVoices();
        OnPropertyChanged(nameof(SelectedSpokenFeedbackProviderOption));

        if (_isProgrammaticRefresh)
        {
            return;
        }

        value = NormalizeProviderId(value);
        _configuredSpokenFeedbackProviderId = value;
        _configuredSpokenFeedbackVoiceId =
            SelectedSpokenFeedbackVoiceId ?? SpeechFeedbackService.DefaultVoiceOptionId;
        _speechFeedback.SelectVoice(value, _configuredSpokenFeedbackVoiceId);

        var selectedVoiceId = NormalizeVoiceIdForSettings(
            _configuredSpokenFeedbackVoiceId
        );
        if (
            _settings.Current.SpokenFeedbackProviderId == value
            && _settings.Current.SpokenFeedbackVoiceId == selectedVoiceId
        )
        {
            return;
        }

        _settings.Update(current =>
            current with { SpokenFeedbackProviderId = value, SpokenFeedbackVoiceId = selectedVoiceId }
        );
    }

    partial void OnSelectedSpokenFeedbackVoiceIdChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedSpokenFeedbackVoiceOption));
        if (_isProgrammaticRefresh)
        {
            return;
        }

        _configuredSpokenFeedbackVoiceId =
            value ?? SpeechFeedbackService.DefaultVoiceOptionId;
        _speechFeedback.SelectVoice(SelectedSpokenFeedbackProviderId, value);
        var normalized = NormalizeVoiceIdForSettings(value);
        if (_settings.Current.SpokenFeedbackVoiceId == normalized)
        {
            return;
        }

        _settings.Update(current => current with { SpokenFeedbackVoiceId = normalized });
    }

    partial void OnSaveToHistoryEnabledChanged(bool value)
    {
        if (_settings.Current.SaveToHistoryEnabled == value)
        {
            return;
        }

        _settings.Update(current => current with { SaveToHistoryEnabled = value });
    }

    partial void OnCaptureLlmProvenanceChanged(bool value)
    {
        if (_settings.Current.CaptureLlmProvenance == value)
        {
            return;
        }

        _settings.Update(current => current with { CaptureLlmProvenance = value });
    }

    partial void OnSelectedHistoryRetentionChanged(HistoryRetentionOption? value)
    {
        if (_isProgrammaticRefresh || value is null)
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

        _settings.Update(current =>
            current with
            {
                HistoryRetentionMode = value.Mode,
                HistoryRetentionMinutes =
                value.Minutes ?? current.HistoryRetentionMinutes,
            }
        );
    }

    private void OnInterfaceLanguageChanged(object? sender, EventArgs e)
    {
        var autoUnloadSeconds =
            SelectedAutoUnloadOption?.Seconds ?? _settings.Current.ModelAutoUnloadSeconds;
        var retentionMode =
            SelectedHistoryRetention?.Mode ?? _settings.Current.HistoryRetentionMode;
        var retentionMinutes =
            SelectedHistoryRetention?.Minutes ?? _settings.Current.HistoryRetentionMinutes;

        RunProgrammaticRefresh(() =>
        {
            AutoUnloadOptions = CreateAutoUnloadOptions();
            HistoryRetentionOptions = CreateHistoryRetentionOptions();
            OnPropertyChanged(nameof(AutoUnloadOptions));
            OnPropertyChanged(nameof(HistoryRetentionOptions));

            SelectedAutoUnloadOption =
                AutoUnloadOptions.FirstOrDefault(option => option.Seconds == autoUnloadSeconds)
                ?? AutoUnloadOptions[0];
            SelectedHistoryRetention = MatchRetention(retentionMode, retentionMinutes);

            // The voices list carries a localized "System default voice" entry, so it
            // must be rebuilt too or the dropdown stays in the previous language.
            RefreshSpokenFeedbackVoices();

            OnPropertyChanged(nameof(SpokenFeedbackHint));
            OnPropertyChanged(nameof(MemoryHint));
        });
    }

    private static IReadOnlyList<AutoUnloadOption> CreateAutoUnloadOptions()
    {
        return
        [
            new(0, Loc.Instance["Advanced.AutoUnloadNever"]),
            new(30, Loc.Instance["Advanced.AutoUnload30Seconds"]),
            new(60, Loc.Instance["Advanced.AutoUnload1Minute"]),
            new(300, Loc.Instance["Advanced.AutoUnload5Minutes"]),
            new(900, Loc.Instance["Advanced.AutoUnload15Minutes"]),
        ];
    }

    private static IReadOnlyList<HistoryRetentionOption> CreateHistoryRetentionOptions()
    {
        return
        [
            new(HistoryRetentionMode.Duration, 24 * 60, Loc.Instance["Advanced.Retention1Day"]),
            new(HistoryRetentionMode.Duration, 7 * 24 * 60, Loc.Instance["Advanced.Retention7Days"]),
            new(HistoryRetentionMode.Duration, 30 * 24 * 60, Loc.Instance["Advanced.Retention30Days"]),
            new(HistoryRetentionMode.Duration, 90 * 24 * 60, Loc.Instance["Advanced.Retention90Days"]),
            new(HistoryRetentionMode.Forever, null, Loc.Instance["Advanced.RetentionForever"]),
            new(HistoryRetentionMode.UntilAppCloses, null, Loc.Instance["Advanced.RetentionUntilAppCloses"]),
        ];
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
        RunProgrammaticRefresh(() =>
        {
            ReplaceCollection(SpokenFeedbackProviders, _speechFeedback.AvailableProviders);
            ApplyEffectiveSpokenFeedbackPreference();
        });
    }

    private void RefreshSpokenFeedbackVoices()
    {
        RunProgrammaticRefresh(() =>
        {
            ReplaceCollection(
                SpokenFeedbackVoices,
                _speechFeedback.GetVoiceOptions(SelectedSpokenFeedbackProviderId)
            );
            var preferredVoiceId =
                string.Equals(
                    SelectedSpokenFeedbackProviderId,
                    _configuredSpokenFeedbackProviderId,
                    StringComparison.Ordinal
                )
                    ? _configuredSpokenFeedbackVoiceId
                    : _speechFeedback.GetSelectedVoiceId(SelectedSpokenFeedbackProviderId);
            var selectedVoiceId = SpokenFeedbackVoices.Any(voice =>
                voice.Id == preferredVoiceId
            )
                ? preferredVoiceId
                : SpeechFeedbackService.DefaultVoiceOptionId;
            SelectedSpokenFeedbackVoiceId = selectedVoiceId;
            OnPropertyChanged(nameof(SelectedSpokenFeedbackVoiceOption));
        });
    }

    private void ApplyEffectiveSpokenFeedbackPreference()
    {
        SelectedSpokenFeedbackProviderId =
            SpokenFeedbackProviders.FirstOrDefault(provider =>
                string.Equals(
                    provider.Id,
                    _configuredSpokenFeedbackProviderId,
                    StringComparison.Ordinal
                )
            )?.Id
            ?? SpokenFeedbackProviders.FirstOrDefault(provider =>
                string.Equals(
                    provider.Id,
                    AppSettings.DefaultSpokenFeedbackProviderId,
                    StringComparison.Ordinal
                )
            )?.Id
            ?? SpokenFeedbackProviders.FirstOrDefault()?.Id
            ?? AppSettings.DefaultSpokenFeedbackProviderId;
        RefreshSpokenFeedbackVoices();
        OnPropertyChanged(nameof(SelectedSpokenFeedbackProviderOption));
    }

    private static string NormalizeProviderId(string? providerId)
    {
        return string.IsNullOrWhiteSpace(providerId)
            ? AppSettings.DefaultSpokenFeedbackProviderId
            : providerId;
    }

    private static string? NormalizeVoiceIdForSettings(string? voiceId)
    {
        return SpeechFeedbackService.IsDefaultVoiceOptionId(voiceId) ? null : voiceId;
    }

    private void RunProgrammaticRefresh(Action refresh)
    {
        var wasProgrammaticRefresh = _isProgrammaticRefresh;
        _isProgrammaticRefresh = true;
        try
        {
            refresh();
        }
        finally
        {
            _isProgrammaticRefresh = wasProgrammaticRefresh;
        }
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
