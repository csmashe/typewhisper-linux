using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AdvancedSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly SpeechFeedbackService _speechFeedback;
    private readonly PluginManager _pluginManager;

    [ObservableProperty] private bool _memoryEnabled;
    [ObservableProperty] private AutoUnloadOption? _selectedAutoUnloadOption;
    [ObservableProperty] private bool _spokenFeedbackEnabled;
    [ObservableProperty] private bool _saveToHistoryEnabled;
    [ObservableProperty] private HistoryRetentionOption? _selectedHistoryRetention;
    [ObservableProperty] private string _selectedSpokenFeedbackProviderId = AppSettings.DefaultSpokenFeedbackProviderId;
    [ObservableProperty] private string? _selectedSpokenFeedbackVoiceId;

    public ObservableCollection<TtsProviderOption> SpokenFeedbackProviders { get; } = [];
    public ObservableCollection<TtsVoiceOption> SpokenFeedbackVoices { get; } = [];

    public TtsProviderOption? SelectedSpokenFeedbackProviderOption
    {
        get => SpokenFeedbackProviders.FirstOrDefault(provider =>
            string.Equals(provider.Id, SelectedSpokenFeedbackProviderId, StringComparison.Ordinal));
        set
        {
            if (value is null || string.Equals(value.Id, SelectedSpokenFeedbackProviderId, StringComparison.Ordinal))
                return;

            SelectedSpokenFeedbackProviderId = value.Id;
            OnPropertyChanged();
        }
    }

    public TtsVoiceOption? SelectedSpokenFeedbackVoiceOption
    {
        get => SpokenFeedbackVoices.FirstOrDefault(voice =>
            string.Equals(voice.Id, SelectedSpokenFeedbackVoiceId, StringComparison.Ordinal));
        set
        {
            if (value is null || string.Equals(value.Id, SelectedSpokenFeedbackVoiceId, StringComparison.Ordinal))
                return;

            SelectedSpokenFeedbackVoiceId = value.Id;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<AutoUnloadOption> AutoUnloadOptions { get; } =
    [
        new(0, "Never"),
        new(30, "30 seconds"),
        new(60, "1 minute"),
        new(300, "5 minutes"),
        new(900, "15 minutes")
    ];

    public IReadOnlyList<HistoryRetentionOption> HistoryRetentionOptions { get; } =
    [
        new(HistoryRetentionMode.Duration, 24 * 60, "1 day"),
        new(HistoryRetentionMode.Duration, 7 * 24 * 60, "7 days"),
        new(HistoryRetentionMode.Duration, 30 * 24 * 60, "30 days"),
        new(HistoryRetentionMode.Duration, 90 * 24 * 60, "90 days"),
        new(HistoryRetentionMode.Forever, null, "Forever"),
        new(HistoryRetentionMode.UntilAppCloses, null, "Until app closes")
    ];

    public bool CanUseSpokenFeedback => _speechFeedback.IsAvailable;
    public bool ShowSpokenFeedbackUnavailableReason => !CanUseSpokenFeedback;
    public string SpokenFeedbackUnavailableReason => "Unavailable: install espeak-ng, espeak, or speech-dispatcher.";
    public string SpokenFeedbackHint => CanUseSpokenFeedback
        ? $"Spoken feedback reads the final transcription aloud via {_speechFeedback.BackendName}."
        : SpokenFeedbackUnavailableReason;

    public bool CanUseMemory => _pluginManager.GetPlugins<IMemoryStoragePlugin>().Any()
                                && _pluginManager.LlmProviders.Any(provider => provider.IsAvailable);
    public bool ShowMemoryUnavailableReason => !CanUseMemory;
    public string MemoryUnavailableReason => "Unavailable: enable a memory storage plugin and configure an LLM provider.";
    public string MemoryHint => CanUseMemory
        ? "Sends each eligible transcription to the configured LLM provider to extract lasting facts. With cloud providers, transcript text is uploaded off-device and stored as memory."
        : MemoryUnavailableReason;

    public AdvancedSectionViewModel(
        ISettingsService settings,
        SpeechFeedbackService speechFeedback,
        PluginManager pluginManager)
    {
        _settings = settings;
        _speechFeedback = speechFeedback;
        _pluginManager = pluginManager;
        _speechFeedback.ProvidersChanged += (_, _) => RefreshSpokenFeedbackProviders();
        Refresh(settings.Current);
        RefreshSpokenFeedbackProviders();
        _settings.SettingsChanged += Refresh;
        _pluginManager.PluginStateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUseMemory));
            OnPropertyChanged(nameof(ShowMemoryUnavailableReason));
            OnPropertyChanged(nameof(MemoryHint));
            OnPropertyChanged(nameof(CanUseSpokenFeedback));
            OnPropertyChanged(nameof(ShowSpokenFeedbackUnavailableReason));
            OnPropertyChanged(nameof(SpokenFeedbackHint));
            RefreshSpokenFeedbackProviders();
            if (!CanUseMemory && MemoryEnabled)
                MemoryEnabled = false;
        };
    }

    private void Refresh(AppSettings settings)
    {
        MemoryEnabled = settings.MemoryEnabled && CanUseMemory;
        SpokenFeedbackEnabled = settings.SpokenFeedbackEnabled && CanUseSpokenFeedback;
        SaveToHistoryEnabled = settings.SaveToHistoryEnabled;
        SelectedSpokenFeedbackProviderId = string.IsNullOrWhiteSpace(settings.SpokenFeedbackProviderId)
            ? AppSettings.DefaultSpokenFeedbackProviderId
            : settings.SpokenFeedbackProviderId;
        SelectedSpokenFeedbackVoiceId = settings.SpokenFeedbackVoiceId ?? SpeechFeedbackService.DefaultVoiceOptionId;
        SelectedAutoUnloadOption = AutoUnloadOptions.FirstOrDefault(option => option.Seconds == settings.ModelAutoUnloadSeconds)
            ?? AutoUnloadOptions[0];
        SelectedHistoryRetention = MatchRetention(settings.HistoryRetentionMode, settings.HistoryRetentionMinutes);
    }

    partial void OnMemoryEnabledChanged(bool value)
    {
        if (_settings.Current.MemoryEnabled == value)
            return;

        if (value && !CanUseMemory)
        {
            MemoryEnabled = false;
            return;
        }

        _settings.Save(_settings.Current with { MemoryEnabled = value });
    }

    partial void OnSelectedAutoUnloadOptionChanged(AutoUnloadOption? value)
    {
        if (value is null || _settings.Current.ModelAutoUnloadSeconds == value.Seconds)
            return;

        _settings.Save(_settings.Current with { ModelAutoUnloadSeconds = value.Seconds });
    }

    partial void OnSpokenFeedbackEnabledChanged(bool value)
    {
        if (_settings.Current.SpokenFeedbackEnabled == value)
            return;

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
            value = AppSettings.DefaultSpokenFeedbackProviderId;

        RefreshSpokenFeedbackVoices();

        if (_settings.Current.SpokenFeedbackProviderId == value)
            return;

        var selectedVoiceId = SpeechFeedbackService.IsDefaultVoiceOptionId(SelectedSpokenFeedbackVoiceId)
            ? null
            : SelectedSpokenFeedbackVoiceId;
        _settings.Save(_settings.Current with
        {
            SpokenFeedbackProviderId = value,
            SpokenFeedbackVoiceId = selectedVoiceId
        });
        OnPropertyChanged(nameof(SelectedSpokenFeedbackProviderOption));
    }

    partial void OnSelectedSpokenFeedbackVoiceIdChanged(string? value)
    {
        _speechFeedback.SelectVoice(SelectedSpokenFeedbackProviderId, value);
        var normalized = SpeechFeedbackService.IsDefaultVoiceOptionId(value) ? null : value;
        if (_settings.Current.SpokenFeedbackVoiceId == normalized)
            return;

        _settings.Save(_settings.Current with { SpokenFeedbackVoiceId = normalized });
        OnPropertyChanged(nameof(SelectedSpokenFeedbackVoiceOption));
    }

    partial void OnSaveToHistoryEnabledChanged(bool value)
    {
        if (_settings.Current.SaveToHistoryEnabled == value)
            return;

        _settings.Save(_settings.Current with { SaveToHistoryEnabled = value });
    }

    partial void OnSelectedHistoryRetentionChanged(HistoryRetentionOption? value)
    {
        if (value is null)
            return;

        if (_settings.Current.HistoryRetentionMode == value.Mode
            && (value.Mode != HistoryRetentionMode.Duration
                || _settings.Current.HistoryRetentionMinutes == value.Minutes))
            return;

        _settings.Save(_settings.Current with
        {
            HistoryRetentionMode = value.Mode,
            HistoryRetentionMinutes = value.Minutes ?? _settings.Current.HistoryRetentionMinutes
        });
    }

    private HistoryRetentionOption MatchRetention(HistoryRetentionMode mode, int minutes) =>
        HistoryRetentionOptions.FirstOrDefault(option =>
            option.Mode == mode && (mode != HistoryRetentionMode.Duration || option.Minutes == minutes))
        ?? HistoryRetentionOptions.FirstOrDefault(option =>
            option.Mode == AppSettings.Default.HistoryRetentionMode
            && option.Minutes == AppSettings.Default.HistoryRetentionMinutes)
        ?? HistoryRetentionOptions[0];

    private void RefreshSpokenFeedbackProviders()
    {
        ReplaceCollection(SpokenFeedbackProviders, _speechFeedback.AvailableProviders);
        if (SpokenFeedbackProviders.All(provider => provider.Id != SelectedSpokenFeedbackProviderId))
            SelectedSpokenFeedbackProviderId = AppSettings.DefaultSpokenFeedbackProviderId;
        RefreshSpokenFeedbackVoices();
        OnPropertyChanged(nameof(SelectedSpokenFeedbackProviderOption));
    }

    private void RefreshSpokenFeedbackVoices()
    {
        ReplaceCollection(SpokenFeedbackVoices, _speechFeedback.GetVoiceOptions(SelectedSpokenFeedbackProviderId));
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
            return;

        target.Clear();
        foreach (var item in snapshot)
            target.Add(item);
    }
}

public sealed record AutoUnloadOption(int Seconds, string DisplayName);

public sealed record HistoryRetentionOption(
    HistoryRetentionMode Mode,
    int? Minutes,
    string DisplayName);
