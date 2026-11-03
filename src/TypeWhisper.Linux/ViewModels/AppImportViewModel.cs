using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.AppImport;
using TypeWhisper.Linux.Services.AppImport;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod

public sealed record AppImportLineItem(string Entry, string OutcomeLabel);

/// <summary>
///     Reads another app's dictionary, shows what importing it would change, and adds the new entries only
///     when the user confirms. Nothing is written before <see cref="Commit" />.
/// </summary>
public sealed partial class AppImportViewModel : ObservableObject, IDisposable
{
    private readonly IDictionaryService _dictionary;
    private readonly AppImportSource _source;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _path;
    private DictionaryImportReview? _review;
    private bool _loadRunning;
    private bool _disposeRequested;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _addButtonText = "";

    [ObservableProperty]
    private bool _canAdd;

    // Assigned once; a 10,000-entry list added item by item would raise as many change notifications.
    [ObservableProperty]
    private IReadOnlyList<AppImportLineItem> _lines = [];

    public AppImportViewModel(IDictionaryService dictionary, AppImportSource source, string path)
    {
        _dictionary = dictionary;
        _source = source;
        _path = path;
        AddButtonText = Loc.Instance.GetString("AppImport.AddEntries", 0);
    }

    public string Title => Loc.Instance.GetString("AppImport.Title", _source.AppName);
    public string LoadingText => Loc.Instance.GetString("AppImport.Loading", _source.AppName);
    public string SourceText => Loc.Instance.GetString("AppImport.Source", _path);
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    public async Task LoadAsync()
    {
        var token = _cancellation.Token;
        _loadRunning = true;
        try
        {
            var review = await Task.Run(() => BuildReview(token), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (review is null)
            {
                Message = Loc.Instance["AppImport.DictionaryUnreadable"];
                return;
            }

            ShowReview(review);
        }
        catch (OperationCanceledException)
        {
            // Closed while reading; nothing was written.
        }
        catch (AppImportReadException ex)
        {
            Trace.WriteLine($"[AppImport] {_source.AppName}: {ex.Failure}: {ex.Message}");
            Message = ex.Failure switch
            {
                AppImportReadFailure.Unstable => Loc.Instance.GetString("AppImport.Unstable", _source.AppName),
                AppImportReadFailure.TooManyEntries => Loc.Instance.GetString("AppImport.TooManyEntries", _source.AppName),
                AppImportReadFailure.TooLarge => Loc.Instance[_source.TooLargeKey],
                AppImportReadFailure.SqliteUnavailable => Loc.Instance["AppImport.SqliteUnavailable"],
                _ => Loc.Instance[_source.MalformedKey],
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Message = Loc.Instance.GetString("AppImport.NotFound", _path);
        }
        catch (Exception ex)
        {
            // The window's Opened handler is async void, so nothing may escape to the dispatcher.
            Trace.WriteLine($"[AppImport] Could not read '{_path}': {ex}");
            Message = Loc.Instance.GetString("AppImport.ReadFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            _loadRunning = false;
            if (_disposeRequested)
            {
                _cancellation.Dispose();
            }
        }
    }

    private DictionaryImportReview? BuildReview(CancellationToken token)
    {
        var batch = _source.Load(_path, token);
        // An unreadable dictionary reads as empty, which would mark every entry as new.
        return _dictionary.TryGetEntries(out var current)
            ? DictionaryImportReview.For(batch, current, token)
            : null;
    }

    private void ShowReview(DictionaryImportReview review)
    {
        _review = review;
        Lines = review.Lines.Select(line => new AppImportLineItem(
                Describe(line),
                Loc.Instance[$"AppImport.Outcome{line.Outcome}"]
            ))
            .ToList();

        if (review.Lines.Count == 0 && review.SourceExcluded == 0)
        {
            Message = Loc.Instance.GetString("AppImport.Empty", _source.AppName);
            return;
        }

        Summary = Loc.Instance.GetString(
            "AppImport.Summary",
            review.Count(DictionaryImportOutcome.Add),
            review.Count(DictionaryImportOutcome.Duplicate),
            review.Count(DictionaryImportOutcome.Conflict),
            review.Count(DictionaryImportOutcome.Unsupported)
        );
        AddButtonText = Loc.Instance.GetString("AppImport.AddEntries", review.Additions.Count);
        CanAdd = review.Additions.Count > 0;
    }

    private static string Describe(DictionaryImportLine line)
    {
        var original = line.Original.Length == 0 ? Loc.Instance["AppImport.Blank"] : line.Original;
        return line.Kind == DictionaryEntryType.Correction
            ? Loc.Instance.GetString("AppImport.CorrectionEntry", original, line.Replacement)
            : original;
    }

    /// <summary>Adds the reviewed entries and returns the message to show the user.</summary>
    public string Commit()
    {
        if (_review is null || _cancellation.IsCancellationRequested)
        {
            return Loc.Instance["AppImport.NothingToAdd"];
        }

        CanAdd = false;
        try
        {
            return _dictionary.CommitReviewedImport(_review) switch
            {
                DictionaryImportCommitResult.Imported =>
                    Loc.Instance.GetString("AppImport.Added", _review.Additions.Count, _source.AppName),
                DictionaryImportCommitResult.ImportedNotDurable =>
                    Loc.Instance.GetString("AppImport.AddedNotDurable", _review.Additions.Count, _source.AppName),
                DictionaryImportCommitResult.Stale => Loc.Instance["AppImport.Stale"],
                _ => Loc.Instance["AppImport.NothingToAdd"],
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Trace.WriteLine($"[AppImport] Could not save the dictionary: {ex.Message}");
            return Loc.Instance.GetString("AppImport.SaveFailed", ex.Message);
        }
    }

    public void Cancel()
    {
        if (!_disposeRequested)
        {
            _cancellation.Cancel();
        }
    }

    /// <summary>
    ///     Cancels a load still running and disposes the token source once it ends: the read on the thread pool
    ///     may still touch the token after the dialog closes.
    /// </summary>
    public void Dispose()
    {
        Cancel();
        _disposeRequested = true;
        if (!_loadRunning)
        {
            _cancellation.Dispose();
        }
    }
}
