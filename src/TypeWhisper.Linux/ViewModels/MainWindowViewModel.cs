using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly PluginLoader _pluginLoader;
    private readonly DictationOrchestrator _dictation;

    [ObservableProperty]
    private string _statusText = "Ready. Press Ctrl+Shift+Space (or click Toggle Recording) to capture audio.";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string? _lastCapturePath;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public string PluginsPath => TypeWhisperEnvironment.PluginsPath;

    public MainWindowViewModel(PluginLoader pluginLoader, DictationOrchestrator dictation)
    {
        _pluginLoader = pluginLoader;
        _dictation = dictation;

        _dictation.RecordingStateChanged += (_, recording) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsRecording = recording;
                StatusText = recording
                    ? "Recording… press the hotkey again to stop."
                    : "Stopped. Saving WAV…";
            });

        _dictation.RecordingCaptured += (_, path) =>
            Dispatcher.UIThread.Post(() =>
            {
                LastCapturePath = path;
                StatusText = $"Captured: {System.IO.Path.GetFileName(path)}";
            });
    }

    [RelayCommand]
    private async Task ToggleRecording() => await _dictation.ToggleAsync();

    [RelayCommand]
    private void LoadPlugins()
    {
        Plugins.Clear();
        var loaded = _pluginLoader.DiscoverAndLoad([TypeWhisperEnvironment.PluginsPath]);

        foreach (var p in loaded)
        {
            Plugins.Add(new PluginItemViewModel(
                Id: p.Manifest.Id,
                Name: p.Manifest.Name,
                Version: p.Manifest.Version,
                Path: p.PluginDirectory));
        }

        StatusText = loaded.Count == 0
            ? $"No plugins found in {TypeWhisperEnvironment.PluginsPath}."
            : $"Loaded {loaded.Count} plugin(s).";
    }
}

public sealed record PluginItemViewModel(string Id, string Name, string Version, string Path);
