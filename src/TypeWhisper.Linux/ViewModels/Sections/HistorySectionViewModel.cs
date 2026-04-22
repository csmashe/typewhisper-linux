using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class HistorySectionViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly SessionAudioFileService _sessionAudioFiles;
    private readonly AudioPlaybackService _audioPlayback;

    public ObservableCollection<HistoryRecordRow> Records { get; } = [];

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _summary = "";

    public HistorySectionViewModel(
        IHistoryService history,
        SessionAudioFileService sessionAudioFiles,
        AudioPlaybackService audioPlayback)
    {
        _history = history;
        _sessionAudioFiles = sessionAudioFiles;
        _audioPlayback = audioPlayback;
        _history.RecordsChanged += () => Dispatcher.UIThread.Post(Refresh);
        _audioPlayback.PlaybackStateChanged += () => Dispatcher.UIThread.Post(RefreshPlaybackState);
        _ = _history.EnsureLoadedAsync().ContinueWith(_ => Dispatcher.UIThread.Post(Refresh));
        Refresh();
    }

    private void Refresh()
    {
        Records.Clear();
        var source = string.IsNullOrWhiteSpace(SearchQuery)
            ? _history.Records
            : _history.Search(SearchQuery);
        foreach (var r in source.OrderByDescending(r => r.Timestamp))
            Records.Add(new HistoryRecordRow(r, this));
        Summary = $"{_history.TotalRecords} record(s) · {_history.TotalWords} word(s) · {TimeSpan.FromSeconds(_history.TotalDuration):hh\\:mm\\:ss}";
    }

    private void RefreshPlaybackState()
    {
        foreach (var record in Records)
            record.NotifyPlaybackStateChanged();
    }

    partial void OnSearchQueryChanged(string value) => Refresh();

    [RelayCommand]
    private void DeleteRecord(HistoryRecordRow record) => _history.DeleteRecord(record.Record.Id);

    [RelayCommand]
    private void TogglePlayback(HistoryRecordRow record)
    {
        if (!record.HasSessionAudio || string.IsNullOrWhiteSpace(record.Record.AudioFileName))
            return;

        if (record.IsPlaying)
            _audioPlayback.Stop();
        else
            _audioPlayback.Play(record.Record.AudioFileName);
    }

    [RelayCommand]
    private void ClearAll() => _history.ClearAll();

    internal bool HasSessionAudio(TranscriptionRecord record) => _sessionAudioFiles.HasAudio(record.AudioFileName);

    internal bool IsPlaying(TranscriptionRecord record) =>
        _audioPlayback.IsPlaying
        && !string.IsNullOrWhiteSpace(_audioPlayback.CurrentFile)
        && string.Equals(_audioPlayback.CurrentFile, record.AudioFileName, StringComparison.OrdinalIgnoreCase);
}

public sealed class HistoryRecordRow : ObservableObject
{
    private readonly HistorySectionViewModel _owner;

    public TranscriptionRecord Record { get; }
    public bool HasSessionAudio => _owner.HasSessionAudio(Record);
    public bool IsPlaying => _owner.IsPlaying(Record);
    public string PlaybackButtonText => IsPlaying ? "Stop" : "Play";

    public HistoryRecordRow(TranscriptionRecord record, HistorySectionViewModel owner)
    {
        Record = record;
        _owner = owner;
    }

    internal void NotifyPlaybackStateChanged()
    {
        OnPropertyChanged(nameof(HasSessionAudio));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlaybackButtonText));
    }
}
