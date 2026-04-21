using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AudioSectionViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly ISettingsService _settings;

    public ObservableCollection<AudioInputDevice> Devices { get; } = [];

    [ObservableProperty] private AudioInputDevice? _selectedDevice;
    [ObservableProperty] private bool _audioDuckingEnabled;
    [ObservableProperty] private bool _pauseMediaDuringRecording;
    [ObservableProperty] private string _statusMessage = "";

    public AudioSectionViewModel(AudioRecordingService audio, ISettingsService settings)
    {
        _audio = audio;
        _settings = settings;

        AudioDuckingEnabled = settings.Current.AudioDuckingEnabled;
        PauseMediaDuringRecording = settings.Current.PauseMediaDuringRecording;

        RefreshDevices();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var d in _audio.GetInputDevices())
            Devices.Add(d);

        var preferred = _settings.Current.SelectedMicrophoneDevice;
        SelectedDevice = Devices.FirstOrDefault(d => d.Index == preferred) ?? Devices.FirstOrDefault(d => d.IsDefault) ?? Devices.FirstOrDefault();
        StatusMessage = Devices.Count == 0
            ? "No input devices detected."
            : $"{Devices.Count} input device(s) available.";
    }

    partial void OnSelectedDeviceChanged(AudioInputDevice? value)
    {
        if (value is null) return;
        _audio.SelectedDeviceIndex = value.Index;
        _settings.Save(_settings.Current with { SelectedMicrophoneDevice = value.Index });
    }

    partial void OnAudioDuckingEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { AudioDuckingEnabled = value });

    partial void OnPauseMediaDuringRecordingChanged(bool value)
        => _settings.Save(_settings.Current with { PauseMediaDuringRecording = value });
}
