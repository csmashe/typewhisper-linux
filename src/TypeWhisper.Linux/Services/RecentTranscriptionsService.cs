using Avalonia.Threading;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Services;

public sealed class RecentTranscriptionsService
{
    private const int FocusRestorePollAttempts = 11;
    private static readonly TimeSpan s_focusRestorePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_focusRestoreTimeout = TimeSpan.FromSeconds(1);

    // Lets the tray menu close and focus return to the window it was opened over.
    private static readonly TimeSpan s_trayMenuCloseDelay = TimeSpan.FromMilliseconds(250);

    // Panels and shells that can hold focus right after a tray menu closes; they never
    // receive a pasted transcript.
    private static readonly HashSet<string> s_desktopShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "gnome-shell", "plasmashell", "xfce4-panel", "mate-panel", "lxqt-panel", "cinnamon",
        "budgie-panel", "waybar", "ironbar", "nwg-panel",
    };

    private readonly Func<string?> _activeWindowIdProvider;
    private readonly Func<CancellationToken, Task<ActiveWindowSnapshot?>>
        _activeWindowSnapshotProvider;
    private readonly Func<bool> _autoPasteProvider;
    private readonly Func<bool> _inputAllowedProvider;
    private readonly Func<string?, TextInsertionStrategy> _insertionStrategyProvider;
    private readonly string _ownProcessName;
    private readonly SemaphoreSlim _pasteLastGate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IHistoryService _history;
    private readonly Func<TextInsertionRequest, Task<InsertionResult>> _insertTextAsync;
    private readonly bool _isWaylandSession;
    private readonly Func<RecentTranscriptionPasteToolHint> _pasteToolHintProvider;
    private readonly RecentTranscriptionStore _store;

    private bool _paletteOpening;
    private RecentTranscriptionsPaletteWindow? _paletteWindow;

    public RecentTranscriptionsService(
        IHistoryService history,
        RecentTranscriptionStore store,
        TextInsertionService textInsertion,
        ISettingsService settings,
        ActiveWindowService activeWindow,
        SystemCommandAvailabilityService commands,
        ISessionActivityMonitor sessionActivity
    )
        : this(
            history,
            store,
            () => settings.Current.AutoPaste,
            activeWindow.GetActiveWindowId,
            activeWindow.GetActiveWindowSnapshotAsync,
            textInsertion.InsertTextAsync,
            Task.Delay,
            () => PasteToolHintFor(commands.GetSnapshot()),
            WaylandSessionDetector.IsWaylandSession(),
            () => sessionActivity.IsInputAllowed,
            processName => AppInsertionStrategyResolver.Resolve(
                settings.Current.AppInsertionStrategies,
                processName
            ),
            CurrentProcessName()
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
        Func<RecentTranscriptionPasteToolHint>? pasteToolHintProvider = null,
        bool isWaylandSession = false,
        Func<bool>? inputAllowedProvider = null,
        Func<string?, TextInsertionStrategy>? insertionStrategyProvider = null,
        string ownProcessName = "typewhisper"
    )
    {
        _history = history;
        _store = store;
        _autoPasteProvider = autoPasteProvider;
        _activeWindowIdProvider = activeWindowIdProvider;
        _activeWindowSnapshotProvider = activeWindowSnapshotProvider;
        _insertTextAsync = insertTextAsync;
        _delay = delay;
        _pasteToolHintProvider =
            pasteToolHintProvider ?? (() => RecentTranscriptionPasteToolHint.X11);
        _isWaylandSession = isWaylandSession;
        _inputAllowedProvider = inputAllowedProvider ?? (static () => true);
        _insertionStrategyProvider =
            insertionStrategyProvider ?? (static _ => TextInsertionStrategy.Auto);
        _ownProcessName = ProcessNameNormalizer.Normalize(ownProcessName);
    }

    private static string CurrentProcessName()
    {
        using var process = Process.GetCurrentProcess();
        return process.ProcessName;
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
            FeedbackRequested?.Invoke(
                Localization.Loc.Instance["Overlay.NoRecentTranscriptions"],
                false
            );
            return;
        }

        var result = await _insertTextAsync(
            new TextInsertionRequest(entry.FinalText, AutoPaste: false)
        );
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
    }

    /// <summary>
    ///     Inserts the latest transcription through normal insertion, without new history or LLM
    ///     work. The tray only copies when focus lands on a panel, TypeWhisper or an unknown app.
    ///     <paramref name="isBusy" /> reports work whose clipboard use this paste could race.
    /// </summary>
    public async Task<LastTranscriptionPasteOutcome> PasteLastTranscriptionAsync(
        LastTranscriptionPasteTrigger trigger,
        Func<bool> isBusy
    )
    {
        if (!await _pasteLastGate.WaitAsync(0))
        {
            return LastTranscriptionPasteOutcome.Ignored;
        }

        try
        {
            if (!_inputAllowedProvider())
            {
                return LastTranscriptionPasteOutcome.Ignored;
            }

            if (isBusy())
            {
                return ReportPasteLastBusy();
            }

            var entry = _store.LatestEntry(_history.Records);
            if (entry is null)
            {
                FeedbackRequested?.Invoke(
                    Localization.Loc.Instance["Overlay.NoRecentTranscriptions"],
                    false
                );
                return LastTranscriptionPasteOutcome.Empty;
            }

            if (trigger is LastTranscriptionPasteTrigger.Tray)
            {
                await _delay(s_trayMenuCloseDelay, CancellationToken.None);
            }

            var target = await CaptureInsertionTargetAsync();

            // Rechecked after the waits above: a dictation or a screen lock may have begun since.
            if (!_inputAllowedProvider())
            {
                return LastTranscriptionPasteOutcome.Ignored;
            }

            if (isBusy())
            {
                return ReportPasteLastBusy();
            }

            // A shortcut fires in the window that should receive the text, so its target needs no
            // verification; a tray click has to land back on an ordinary app window.
            var focusVerified =
                trigger is LastTranscriptionPasteTrigger.Shortcut
                || IsEligibleTrayTarget(target);
            await InsertAsync(entry.FinalText, target, focusVerified);
            return LastTranscriptionPasteOutcome.Delivered;
        }
        finally
        {
            _pasteLastGate.Release();
        }
    }

    public event Action<string, bool>? FeedbackRequested;

    private LastTranscriptionPasteOutcome ReportPasteLastBusy()
    {
        FeedbackRequested?.Invoke(
            Localization.Loc.Instance["RecentTranscriptions.PasteLastBusy"],
            false
        );
        return LastTranscriptionPasteOutcome.Busy;
    }

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
            FeedbackRequested?.Invoke(
                Localization.Loc.Instance["Overlay.NoRecentTranscriptions"],
                false
            );
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
        var focusVerified =
            !_autoPasteProvider()
            || target.Snapshot is not null && await WaitForFocusRestorationAsync(target.Snapshot)
            || !string.IsNullOrWhiteSpace(target.WindowId)
                && (target.Snapshot is null || !_isWaylandSession);

        return await InsertAsync(entry.FinalText, target, focusVerified);
    }

    private async Task<InsertionResult> InsertAsync(
        string text,
        RecentTranscriptionInsertionTarget target,
        bool focusVerified
    )
    {
        // Process and title drive terminal detection and the per-app strategy, as for a dictation.
        // Without a process, multiline text could be typed into an unrecognised terminal and run.
        var processName = target.Snapshot?.ProcessName;
        if (processName is null && text.AsSpan().ContainsAny('\r', '\n'))
        {
            focusVerified = false;
        }

        var request = focusVerified
            ? new TextInsertionRequest(
                text,
                _autoPasteProvider(),
                target.WindowId,
                processName,
                target.Snapshot?.Title,
                Strategy: _insertionStrategyProvider(processName)
            )
            : new TextInsertionRequest(text, AutoPaste: false);
        var result = await _insertTextAsync(request);
        FeedbackRequested?.Invoke(StatusTextFor(result), IsError(result));
        return result;
    }

    private bool IsEligibleTrayTarget(RecentTranscriptionInsertionTarget target)
    {
        // A bare window id could still be a panel or TypeWhisper, so the app must be identified.
        if (target.Snapshot is not { } snapshot)
        {
            return false;
        }

        var process = ProcessNameNormalizer.Normalize(snapshot.ProcessName ?? snapshot.AppId);
        return process.Length > 0
               && !s_desktopShellProcesses.Contains(process)
               && !string.Equals(process, _ownProcessName, StringComparison.OrdinalIgnoreCase);
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

        // ReSharper disable once InvertIf -- kept symmetrical with the identical AppId block above.
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
        // Inverted so an unrecognized result reports failure instead of claiming the text landed.
        // ActionHandled is a success: a plugin action consumed the text in place of insertion.
        return result
            is not (InsertionResult.Typed
                or InsertionResult.Pasted
                or InsertionResult.CopiedToClipboard
                or InsertionResult.NoText
                or InsertionResult.ActionHandled);
    }

    private string StatusTextFor(InsertionResult result)
    {
        return result switch
        {
            InsertionResult.Typed =>
                Localization.Loc.Instance["RecentTranscriptions.Typed"],
            InsertionResult.Pasted =>
                Localization.Loc.Instance["RecentTranscriptions.Pasted"],
            InsertionResult.CopiedToClipboard =>
                Localization.Loc.Instance["RecentTranscriptions.CopiedToClipboard"],
            InsertionResult.NoText => Localization.Loc.Instance["Overlay.NoRecentTranscriptions"],
            InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
            InsertionResult.MissingPasteTool =>
                Localization.Loc.Instance[PasteToolInstallHintKey(_pasteToolHintProvider())],
            InsertionResult.Failed =>
                Localization.Loc.Instance["RecentTranscriptions.InsertionFailed"],
            _ => Localization.Loc.Instance["Recorder.StatusDone"],
        };
    }

    private static string ClipboardToolMissingMessage()
    {
        var clipboardTool =
            WaylandSessionDetector.HasWaylandDisplay()
                ? "wl-clipboard"
                : "xclip";
        return Localization.Loc.Instance.GetString(
            "TextInsertion.ClipboardInstallHint",
            clipboardTool
        );
    }

    private static RecentTranscriptionPasteToolHint PasteToolHintFor(
        LinuxCapabilitySnapshot snapshot
    )
    {
        if (snapshot.SessionType != "Wayland")
        {
            return RecentTranscriptionPasteToolHint.X11;
        }

        return snapshot.CompositorRejectsWtype
            ? RecentTranscriptionPasteToolHint.WaylandYdotool
            : RecentTranscriptionPasteToolHint.Wayland;
    }

    private static string PasteToolInstallHintKey(RecentTranscriptionPasteToolHint hint)
    {
        return hint switch
        {
            RecentTranscriptionPasteToolHint.Wayland =>
                "RecentTranscriptions.PasteToolInstallHintWayland",
            RecentTranscriptionPasteToolHint.WaylandYdotool =>
                "RecentTranscriptions.PasteToolInstallHintWaylandYdotool",
            _ => "RecentTranscriptions.PasteToolInstallHintX11",
        };
    }
}

internal enum RecentTranscriptionPasteToolHint
{
    X11,
    Wayland,
    WaylandYdotool,
}

public enum LastTranscriptionPasteTrigger
{
    Shortcut,
    Tray,
}

public enum LastTranscriptionPasteOutcome
{
    /// <summary>Handed to normal insertion, which reported its own result.</summary>
    Delivered,
    /// <summary>No transcription to paste.</summary>
    Empty,
    /// <summary>Another dictation or capture could race the clipboard.</summary>
    Busy,
    /// <summary>A paste is already running or input is not allowed (locked session).</summary>
    Ignored,
}

internal sealed record RecentTranscriptionInsertionTarget(
    string? WindowId,
    ActiveWindowSnapshot? Snapshot
);
