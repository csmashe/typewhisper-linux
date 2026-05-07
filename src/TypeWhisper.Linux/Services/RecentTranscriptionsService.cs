using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using Avalonia.Threading;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Services;

public sealed class RecentTranscriptionsService
{
    private const int PaletteLimit = 12;

    private readonly IHistoryService _history;
    private readonly RecentTranscriptionStore _store;
    private readonly TextInsertionService _textInsertion;
    private readonly ISettingsService _settings;
    private readonly ActiveWindowService _activeWindow;
    private readonly SystemCommandAvailabilityService _commands;

    private RecentTranscriptionsPaletteWindow? _paletteWindow;

    public event Action<string, bool>? FeedbackRequested;

    public RecentTranscriptionsService(
        IHistoryService history,
        RecentTranscriptionStore store,
        TextInsertionService textInsertion,
        ISettingsService settings,
        ActiveWindowService activeWindow,
        SystemCommandAvailabilityService commands)
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
        string? appProcessName) =>
        _store.RecordTranscription(id, finalText, timestamp, appName, appProcessName);

    public void TogglePalette()
    {
        Dispatcher.UIThread.Post(TogglePaletteCore);
    }

    private void TogglePaletteCore()
    {
        if (_paletteWindow is { } existingWindow)
        {
            existingWindow.RequestClose();
            return;
        }

        var entries = _store.MergedEntries(_history.Records, PaletteLimit);
        if (entries.Count == 0)
        {
            FeedbackRequested?.Invoke("No recent transcriptions.", false);
            return;
        }

        var targetWindowId = _activeWindow.GetActiveWindowId();
        var viewModel = new RecentTranscriptionsPaletteViewModel(
            entries,
            item => _ = InsertEntryAsync(item.Entry, targetWindowId));
        var window = new RecentTranscriptionsPaletteWindow(viewModel);
        _paletteWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_paletteWindow, window))
                _paletteWindow = null;
        };

        window.Show();
        window.Activate();
    }

    public async Task CopyLastTranscriptionToClipboardAsync()
    {
        var entry = _store.LatestEntry(_history.Records);
        if (entry is null)
        {
            FeedbackRequested?.Invoke("No recent transcriptions.", false);
            return;
        }

        var result = await _textInsertion.InsertTextAsync(entry.FinalText, autoPaste: false);
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
    }

    private async Task InsertEntryAsync(RecentTranscriptionEntry entry, string? targetWindowId)
    {
        var result = await _textInsertion.InsertTextAsync(
            entry.FinalText,
            _settings.Current.AutoPaste,
            targetWindowId);
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
    }

    private static bool IsError(InsertionResult result) =>
        result is InsertionResult.Failed
            or InsertionResult.MissingClipboardTool
            or InsertionResult.MissingPasteTool;

    private string StatusTextFor(InsertionResult result) =>
        result switch
        {
            InsertionResult.Typed => "Typed recent transcription.",
            InsertionResult.Pasted => "Pasted recent transcription.",
            InsertionResult.CopiedToClipboard => "Copied recent transcription to clipboard.",
            InsertionResult.NoText => "No recent transcriptions.",
            InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
            InsertionResult.MissingPasteTool => _commands.GetSnapshot().PasteToolInstallHint,
            InsertionResult.Failed => "Text insertion failed.",
            _ => "Done."
        };

    private static string ClipboardToolMissingMessage() =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Install wl-clipboard to copy recent transcriptions."
            : "Install xclip to copy recent transcriptions.";
}
