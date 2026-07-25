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
    private const int FocusRestorePollAttempts = 11;
    private static readonly TimeSpan s_focusRestorePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_focusRestoreTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<string?> _activeWindowIdProvider;
    private readonly Func<CancellationToken, Task<ActiveWindowSnapshot?>>
        _activeWindowSnapshotProvider;
    private readonly Func<bool> _autoPasteProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IHistoryService _history;
    private readonly Func<TextInsertionRequest, Task<InsertionResult>> _insertTextAsync;
    private readonly bool _isWaylandSession;
    private readonly Func<string> _pasteToolInstallHintProvider;
    private readonly RecentTranscriptionStore _store;

    private bool _paletteOpening;
    private RecentTranscriptionsPaletteWindow? _paletteWindow;

    public RecentTranscriptionsService(
        IHistoryService history,
        RecentTranscriptionStore store,
        TextInsertionService textInsertion,
        ISettingsService settings,
        ActiveWindowService activeWindow,
        SystemCommandAvailabilityService commands
    )
        : this(
            history,
            store,
            () => settings.Current.AutoPaste,
            activeWindow.GetActiveWindowId,
            activeWindow.GetActiveWindowSnapshotAsync,
            textInsertion.InsertTextAsync,
            Task.Delay,
            () => commands.GetSnapshot().PasteToolInstallHint,
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
        )
    {
    }

    internal RecentTranscriptionsService(
        IHistoryService history,
        RecentTranscriptionStore store,
        Func<bool> autoPasteProvider,
        Func<string?> activeWindowIdProvider,
        Func<CancellationToken, Task<ActiveWindowSnapshot?>> activeWindowSnapshotProvider,
        Func<TextInsertionRequest, Task<InsertionResult>> insertTextAsync,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<string>? pasteToolInstallHintProvider = null,
        bool isWaylandSession = false
    )
    {
        _history = history;
        _store = store;
        _autoPasteProvider = autoPasteProvider;
        _activeWindowIdProvider = activeWindowIdProvider;
        _activeWindowSnapshotProvider = activeWindowSnapshotProvider;
        _insertTextAsync = insertTextAsync;
        _delay = delay;
        _pasteToolInstallHintProvider = pasteToolInstallHintProvider ?? (() => "");
        _isWaylandSession = isWaylandSession;
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

        var result = await _insertTextAsync(
            new TextInsertionRequest(entry.FinalText, AutoPaste: false)
        );
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
    }

    public event Action<string, bool>? FeedbackRequested;

    private void TogglePaletteCore()
    {
        TogglePaletteCoreAsync()
            .ContinueWith(
                t =>
                    Trace.WriteLine(
                        $"[RecentTranscriptionsService] TogglePaletteCoreAsync faulted: {t.Exception?.GetBaseException().Message}"
                    ),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
    }

    private async Task TogglePaletteCoreAsync()
    {
        if (_paletteWindow is { } existingWindow)
        {
            existingWindow.RequestClose();
            return;
        }

        if (_paletteOpening)
        {
            return;
        }

        var entries = _store.MergedEntries(_history.Records);
        if (entries.Count == 0)
        {
            FeedbackRequested?.Invoke("No recent transcriptions.", false);
            return;
        }

        _paletteOpening = true;
        try
        {
            // Capture the X11 handle and identity snapshot before the palette can steal focus.
            var target = await CaptureInsertionTargetAsync();
            var viewModel = new RecentTranscriptionsPaletteViewModel(
                entries,
                item => InsertEntryFireAndForget(item.Entry, target)
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
        finally
        {
            _paletteOpening = false;
        }
    }

    internal async Task<RecentTranscriptionInsertionTarget> CaptureInsertionTargetAsync()
    {
        var windowId = _activeWindowIdProvider();
        ActiveWindowSnapshot? snapshot = null;
        try
        {
            snapshot = await _activeWindowSnapshotProvider(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[RecentTranscriptionsService] Active-window capture failed: {ex.Message}"
            );
        }

        if (_isWaylandSession)
        {
            // xdotool only sees stale/XWayland state on Wayland, so its window id can't
            // prove focus and must never authorize insertion; drop it and any xdotool
            // snapshot so capture falls back to a compositor-native id or clipboard-only.
            windowId = null;
            if (snapshot is { Source: "xdotool" })
            {
                snapshot = null;
            }
        }
        else if (snapshot is { Source: "xdotool", WindowId.Length: > 0 })
        {
            // On X11, keep the activation handle in sync with an xdotool-sourced snapshot.
            windowId = snapshot.WindowId;
        }

        return new RecentTranscriptionInsertionTarget(
            windowId,
            HasUsableIdentity(snapshot) ? snapshot : null
        );
    }

    private void InsertEntryFireAndForget(
        RecentTranscriptionEntry entry,
        RecentTranscriptionInsertionTarget target
    )
    {
        InsertEntryAsync(entry, target)
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

    internal async Task<InsertionResult> InsertEntryAsync(
        RecentTranscriptionEntry entry,
        RecentTranscriptionInsertionTarget target
    )
    {
        // Insertion authority, strongest first: verified focus > X11 window-id activation >
        // clipboard-only. An X11 id is trustworthy because insertion re-activates it
        // deterministically before typing; a Wayland id is just a stale xdotool guess, so a
        // failed verification falls back to clipboard-only.
        var autoPaste = _autoPasteProvider();
        var focusVerified =
            !autoPaste
            || target.Snapshot is not null && await WaitForFocusRestorationAsync(target.Snapshot)
            || !string.IsNullOrWhiteSpace(target.WindowId)
                && (target.Snapshot is null || !_isWaylandSession);

        var request = focusVerified
            ? new TextInsertionRequest(
                entry.FinalText,
                autoPaste,
                target.WindowId
            )
            : new TextInsertionRequest(entry.FinalText, AutoPaste: false);
        var result = await _insertTextAsync(request);
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
        return result;
    }

    private async Task<bool> WaitForFocusRestorationAsync(ActiveWindowSnapshot target)
    {
        using var timeout = new CancellationTokenSource(s_focusRestoreTimeout);
        for (var attempt = 0; attempt < FocusRestorePollAttempts; attempt++)
        {
            ActiveWindowSnapshot? current;
            try
            {
                current = await _activeWindowSnapshotProvider(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[RecentTranscriptionsService] Focus verification failed: {ex.Message}"
                );
                current = null;
            }

            if (MatchesTargetIdentity(target, current))
            {
                return true;
            }

            if (attempt == FocusRestorePollAttempts - 1 || timeout.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _delay(s_focusRestorePollInterval, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        return false;
    }

    private static bool HasUsableIdentity(ActiveWindowSnapshot? snapshot)
    {
        return snapshot is not null
               && (
                   !string.IsNullOrWhiteSpace(snapshot.WindowId)
                   || (
                       !string.IsNullOrWhiteSpace(snapshot.Title)
                       && (
                           !string.IsNullOrWhiteSpace(snapshot.AppId)
                           || !string.IsNullOrWhiteSpace(snapshot.ProcessName)
                       )
                   )
               );
    }

    private static bool MatchesTargetIdentity(
        ActiveWindowSnapshot target,
        ActiveWindowSnapshot? current
    )
    {
        if (current is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(target.WindowId))
        {
            return string.Equals(target.WindowId, current.WindowId, StringComparison.Ordinal)
                   && string.Equals(target.Source, current.Source, StringComparison.OrdinalIgnoreCase);
        }

        if (
            string.IsNullOrWhiteSpace(target.Title)
            || !string.Equals(target.Title, current.Title, StringComparison.Ordinal)
        )
        {
            return false;
        }

        var hasAppIdentity = false;
        if (!string.IsNullOrWhiteSpace(target.AppId))
        {
            hasAppIdentity = true;
            if (!string.Equals(target.AppId, current.AppId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(target.ProcessName))
        {
            hasAppIdentity = true;
            if (
                !string.Equals(
                    target.ProcessName,
                    current.ProcessName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }
        }

        return hasAppIdentity;
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
            InsertionResult.MissingPasteTool => _pasteToolInstallHintProvider(),
            InsertionResult.Failed => "Text insertion failed.",
            _ => "Done.",
        };
    }

    private static string ClipboardToolMissingMessage()
    {
        return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Install wl-clipboard to copy recent transcriptions."
            : "Install xclip to copy recent transcriptions.";
    }
}

internal sealed record RecentTranscriptionInsertionTarget(
    string? WindowId,
    ActiveWindowSnapshot? Snapshot
);
