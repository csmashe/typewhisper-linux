using Avalonia.Threading;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Services;

public sealed class RecentTranscriptionsService
{
    private readonly ActiveWindowService _activeWindow;
    private readonly SystemCommandAvailabilityService _commands;

    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly RecentTranscriptionStore _store;
    private readonly TextInsertionService _textInsertion;

    private RecentTranscriptionsPaletteWindow? _paletteWindow;

    public RecentTranscriptionsService(
        IHistoryService history,
        RecentTranscriptionStore store,
        TextInsertionService textInsertion,
        ISettingsService settings,
        ActiveWindowService activeWindow,
        SystemCommandAvailabilityService commands
    )
    {
        _history = history;
        _store = store;
        _textInsertion = textInsertion;
        _settings = settings;
        _activeWindow = activeWindow;
        _commands = commands;
    }

    public void RecordTranscription(
        string id,
        string finalText,
        DateTime timestamp,
        string? appName,
        string? appProcessName
    )
    {
        _store.RecordTranscription(id, finalText, timestamp, appName, appProcessName);
    }

    public void TogglePalette()
    {
        Dispatcher.UIThread.Post(TogglePaletteCore);
    }

    public async Task CopyLastTranscriptionToClipboardAsync()
    {
        var entry = _store.LatestEntry(_history.Records);
        if (entry is null)
        {
            FeedbackRequested?.Invoke("No recent transcriptions.", false);
            return;
        }

        var result = await _textInsertion.InsertTextAsync(entry.FinalText, false);
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
    }

    public event Action<string, bool>? FeedbackRequested;

    private void TogglePaletteCore()
    {
        if (_paletteWindow is { } existingWindow)
        {
            existingWindow.RequestClose();
            return;
        }

        var entries = _store.MergedEntries(_history.Records);
        if (entries.Count == 0)
        {
            FeedbackRequested?.Invoke("No recent transcriptions.", false);
            return;
        }

        // Capture the focused window ID before the palette steals focus,
        // so InsertEntryAsync can refocus the original app when inserting.
        var targetWindowId = _activeWindow.GetActiveWindowId();
        var viewModel = new RecentTranscriptionsPaletteViewModel(
            entries,
            item => InsertEntryFireAndForget(item.Entry, targetWindowId)
        );
        var window = new RecentTranscriptionsPaletteWindow(viewModel);
        _paletteWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_paletteWindow, window))
            {
                _paletteWindow = null;
            }
        };

        window.Show();
        window.Activate();
    }

    private void InsertEntryFireAndForget(RecentTranscriptionEntry entry, string? targetWindowId)
    {
        InsertEntryAsync(entry, targetWindowId)
            .ContinueWith(
                t =>
                    Trace.WriteLine(
                        $"[RecentTranscriptionsService] InsertEntryAsync faulted: {t.Exception?.GetBaseException().Message}"
                    ),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
    }

    private async Task InsertEntryAsync(RecentTranscriptionEntry entry, string? targetWindowId)
    {
        var result = await _textInsertion.InsertTextAsync(
            entry.FinalText,
            _settings.Current.AutoPaste,
            targetWindowId
        );
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
    }

    private static bool IsError(InsertionResult result)
    {
        return result
            is InsertionResult.Failed
            or InsertionResult.MissingClipboardTool
            or InsertionResult.MissingPasteTool;
    }

    private string StatusTextFor(InsertionResult result)
    {
        return result switch
        {
            InsertionResult.Typed => "Typed recent transcription.",
            InsertionResult.Pasted => "Pasted recent transcription.",
            InsertionResult.CopiedToClipboard => "Copied recent transcription to clipboard.",
            InsertionResult.NoText => Localization.Loc.Instance["Overlay.NoRecentTranscriptions"],
            InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
            InsertionResult.MissingPasteTool => _commands.GetSnapshot().PasteToolInstallHint,
            InsertionResult.Failed => "Text insertion failed.",
            _ => "Done."
        };
    }

    private static string ClipboardToolMissingMessage()
    {
        return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Install wl-clipboard to copy recent transcriptions."
            : "Install xclip to copy recent transcriptions.";
    }
}