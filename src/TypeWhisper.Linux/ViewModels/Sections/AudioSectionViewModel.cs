using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AudioSectionViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly ISettingsService _settings;

    public ObservableCollection<AudioInputDevice> Devices { get; } = [];
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];

    [ObservableProperty] private AudioInputDevice? _selectedDevice;
    [ObservableProperty] private bool _audioDuckingEnabled;
    [ObservableProperty] private bool _pauseMediaDuringRecording;
    [ObservableProperty] private string? _translationTargetLanguage;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private double _previewLevel;
    [ObservableProperty] private bool _isPreviewActive;

    public TranslationTargetOption? SelectedTranslationTargetOption
    {
        get => TranslationTargetOptions.FirstOrDefault(option =>
            string.Equals(option.Code, TranslationTargetLanguage, StringComparison.Ordinal));
        set
        {
            var code = value?.Code;
            if (string.Equals(code, TranslationTargetLanguage, StringComparison.Ordinal))
                return;

            TranslationTargetLanguage = code;
            OnPropertyChanged();
        }
    }

    public AudioSectionViewModel(AudioRecordingService audio, ISettingsService settings)
    {
        _audio = audio;
        _settings = settings;

        AudioDuckingEnabled = settings.Current.AudioDuckingEnabled;
        PauseMediaDuringRecording = settings.Current.PauseMediaDuringRecording;
        TranslationTargetLanguage = settings.Current.TranslationTargetLanguage;
        _audio.LevelChanged += OnLevelChanged;

        foreach (var option in TranslationModelInfo.GlobalTargetOptions)
            TranslationTargetOptions.Add(option);

        RefreshDevices();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var d in _audio.GetInputDevices())
            Devices.Add(d);

        SelectedDevice = _audio.ResolveConfiguredDevice(
            _settings.Current.SelectedMicrophoneDevice,
            _settings.Current.SelectedMicrophoneDeviceId);
        StatusMessage = Devices.Count == 0
            ? "No input devices detected."
            : $"{Devices.Count} input device(s) available.";
    }

    partial void OnSelectedDeviceChanged(AudioInputDevice? value)
    {
        if (value is null) return;

        var restartPreview = IsPreviewActive;
        if (restartPreview)
            _audio.StopPreview();

        _audio.SelectedDeviceIndex = value.Index;
        _settings.Save(_settings.Current with
        {
            SelectedMicrophoneDevice = value.Index,
            SelectedMicrophoneDeviceId = value.PersistentId
        });

        if (restartPreview)
            ActivatePreview();
    }

    partial void OnAudioDuckingEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { AudioDuckingEnabled = value });

    partial void OnPauseMediaDuringRecordingChanged(bool value)
        => _settings.Save(_settings.Current with { PauseMediaDuringRecording = value });

    partial void OnTranslationTargetLanguageChanged(string? value)
    {
        _settings.Save(_settings.Current with { TranslationTargetLanguage = value });
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
    }

    public void ActivatePreview()
    {
        IsPreviewActive = _audio.StartPreview();
        if (!IsPreviewActive && Devices.Count > 0)
            StatusMessage = "Could not start live input preview for the selected microphone.";
    }

    public void DeactivatePreview()
    {
        _audio.StopPreview();
        IsPreviewActive = false;
        PreviewLevel = 0;
    }

    private void OnLevelChanged(object? sender, float level)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PreviewLevel = Math.Clamp(level * 8, 0, 1);
        });
    }
}
