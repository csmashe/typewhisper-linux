using Avalonia.Threading;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Services;

public sealed class TransformSelectionService
{
    private static readonly TimeSpan s_processingTimeout = TimeSpan.FromSeconds(90);
    private readonly IActiveWindowService _activeWindow;
    private readonly AudioRecordingService _audio;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ModelManagerService _models;
    private readonly OverlayCoordinator _overlayCoordinator;
    private readonly Func<TimeSpan, CancellationTokenSource> _processingTimeoutCtsFactory;
    private readonly PromptProcessingService _promptProcessing;
    private readonly ISettingsService _settings;

    private readonly TextInsertionService _textInsertion;
    private readonly Func<string, Task> _showWarningDialog;
    private DictationOverlayState _overlayState = DictationOverlayState.Hidden;
    private OverlayPresentationToken? _overlayToken;

    private TransformSelectionSession? _session;

    public TransformSelectionService(
        TextInsertionService textInsertion,
        AudioRecordingService audio,
        ModelManagerService models,
        PromptProcessingService promptProcessing,
        ISettingsService settings,
        IActiveWindowService activeWindow,
        SystemCommandAvailabilityService commands,
        OverlayCoordinator overlayCoordinator,
        Func<TimeSpan, CancellationTokenSource>? processingTimeoutCtsFactory = null,
        Func<string, Task>? showWarningDialog = null
    )
    {
        _textInsertion = textInsertion;
        _audio = audio;
        _models = models;
        _promptProcessing = promptProcessing;
        _settings = settings;
        _activeWindow = activeWindow;
        _commands = commands;
        _overlayCoordinator = overlayCoordinator;
        _processingTimeoutCtsFactory = processingTimeoutCtsFactory
                                       ?? (timeout => new CancellationTokenSource(timeout));
        _showWarningDialog = showWarningDialog ?? ShowWarningDialogAsync;
    }

    public async Task ToggleAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (_session is null)
            {
                _overlayToken = _overlayCoordinator.Acquire(OverlayRequester.Transform);
                await StartAsync();
            }
            else
            {
                await StopAndTransformAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string BuildTransformPrompt(string selectedText, string command)
    {
        return $"""
                You edit the user's selected text according to their command below.
                Actually apply the command — rewrite, shorten, lengthen, reformat, or restyle the text
                as asked. Do NOT return the text unchanged unless the command explicitly asks you to.
                Keep the original meaning and any essential facts unless the command says otherwise.
                Output ONLY the edited text — no preamble, no quotes, no explanation, no markdown fences.

                Command:
                {command}

                Selected text:
                {selectedText}
                """;
    }

    internal static bool IsCancelCommand(string command)
    {
        var normalized = command.Trim().Trim('.', '!', '?', ',');
        return normalized.Equals("cancel", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("cancel that", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("never mind", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("nevermind", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("stop", StringComparison.OrdinalIgnoreCase);
    }

    // Test seam: lets the terminal-aware copy-shortcut decision be tested without constructing the full service. See audit §3 M5.
    internal static async Task<TransformSelectionCapture> CaptureSelectionForTransformAsync(
        TextInsertionService textInsertion,
        IActiveWindowService activeWindow
    )
    {
        var targetSnapshot = await activeWindow.GetActiveWindowSnapshotAsync(
            CancellationToken.None
        );
        var selectedText = await textInsertion.CaptureSelectedTextAsync(
            TextInsertionService.IsTerminalApp(targetSnapshot?.ProcessName)
        );
        return new TransformSelectionCapture(selectedText, targetSnapshot);
    }

    // Test seam: decides whether it's still safe to replace the captured selection. See audit §3 M6.
    // Cannot detect caret/selection drift within the same window — that would need an
    // AT-SPI selection-range or document-revision token.
    internal static bool HasSelectionTargetChanged(
        ActiveWindowSnapshot? captured,
        ActiveWindowSnapshot? current
    )
    {
        // Missing identity now fails closed to clipboard. Process-name equality used to
        // authorize replacement here, but it cannot distinguish two windows of the same app.
        if (
            captured is null
            || current is null
            || string.IsNullOrWhiteSpace(captured.Source)
            || string.IsNullOrWhiteSpace(current.Source)
            || string.IsNullOrWhiteSpace(captured.WindowId)
            || string.IsNullOrWhiteSpace(current.WindowId)
        )
        {
            return true;
        }

        return !string.Equals(captured.Source, current.Source, StringComparison.Ordinal)
               || !string.Equals(
                   captured.WindowId,
                   current.WindowId,
                   StringComparison.Ordinal
               )
               || !string.Equals(
                   captured.AppId,
                   current.AppId,
                   StringComparison.OrdinalIgnoreCase
               );
    }

    // Test seam: re-checks that the captured window still holds focus. Returns the captured
    // snapshot while replacement is still safe, null once the target changed. The probe swallows
    // per-provider cancellation and reports a null snapshot, so an expired processing deadline is
    // rethrown here into the timeout handling — otherwise it reads as a focus change.
    internal static async Task<ActiveWindowSnapshot?> ResolveReplacementTargetAsync(
        IActiveWindowService activeWindow,
        ActiveWindowSnapshot? capturedSnapshot,
        CancellationToken processingToken
    )
    {
        var currentSnapshot = await activeWindow.GetActiveWindowSnapshotAsync(processingToken);
        processingToken.ThrowIfCancellationRequested();
        return HasSelectionTargetChanged(capturedSnapshot, currentSnapshot)
            ? null
            : capturedSnapshot;
    }

    internal static Task<InsertionResult> DeliverValidatedTransformAsync(
        TextInsertionService textInsertion,
        string transformed,
        ActiveWindowSnapshot targetSnapshot,
        bool isWaylandSession
    )
    {
        var targetWindowId = !isWaylandSession
                             && string.Equals(
                                 targetSnapshot.Source,
                                 "xdotool",
                                 StringComparison.Ordinal
                             )
            ? targetSnapshot.WindowId
            : null;
        return textInsertion.InsertTextAsync(
            transformed,
            true,
            targetWindowId,
            targetSnapshot.ProcessName,
            targetSnapshot.Title
        );
    }

    // Test seam: delivers an aborted transform clipboard-only so it can never paste into the now-focused window. See audit §3 M6.
    internal static Task<InsertionResult> DeliverAbortedTransformAsync(
        TextInsertionService textInsertion,
        string transformed
    ) => textInsertion.InsertTextAsync(transformed, autoPaste: false);

    public event EventHandler<DictationOverlayState>? OverlayStateChanged;

    private async Task StartAsync()
    {
        if (!_promptProcessing.IsAnyProviderAvailable)
        {
            await ShowWarningAsync(
                "No LLM provider available. Please configure an API key in Plugins."
            );
            return;
        }

        var capture = await CaptureSelectionForTransformAsync(_textInsertion, _activeWindow);
        var selectedText = capture.SelectedText;
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            await ShowWarningAsync("Select text before using Transform Selection.");
            return;
        }

        AudioRecordingService.AudioCaptureSession? captureSession;
        try
        {
            captureSession = _audio.TryStartRecording(_settings.Current.WhisperModeEnabled);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TransformSelection] Failed to start recording: {ex}");
            await ShowWarningAsync($"Could not start recording: {ex.Message}");
            return;
        }

        if (captureSession is null)
        {
            await ShowWarningAsync("Could not start recording. Check your microphone settings.");
            return;
        }

        _session = new TransformSelectionSession(
            selectedText,
            capture.TargetSnapshot,
            captureSession
        );
        PublishOverlay(state =>
            state with
            {
                IsOverlayVisible = true,
                ShowFeedback = false,
                FeedbackText = null,
                FeedbackIsError = false,
                IsRecording = true,
                StatusText = Localization.Loc.Instance["Overlay.TransformPrompt"],
                PartialText = selectedText,
                ActiveAppName = ResolveActiveAppName(capture.TargetSnapshot),
                SessionStartedAtUtc = DateTime.UtcNow,
            }
        );
    }

    private async Task StopAndTransformAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        PublishOverlay(state =>
            state with
            {
                IsOverlayVisible = true,
                ShowFeedback = false,
                FeedbackText = null,
                IsRecording = false,
                StatusText = Localization.Loc.Instance["Overlay.TransformProcessing"],
                PartialText = session.SelectedText,
                ActiveAppName = ResolveActiveAppName(session.TargetSnapshot),
                SessionStartedAtUtc = null,
            }
        );

        byte[] wav;
        try
        {
            wav = await _audio.StopRecordingAsync(session.CaptureSession);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TransformSelection] Failed to stop recording: {ex}");
            await ShowWarningAsync($"Could not stop recording: {ex.Message}");
            return;
        }

        if (wav.Length == 0)
        {
            await ShowWarningAsync("No command audio was recorded.");
            return;
        }

        using var processingCts = _processingTimeoutCtsFactory(s_processingTimeout);
        try
        {
            ModelManagerService.TranscriptionLease lease;
            try
            {
                lease = await _models.AcquireTranscriptionAsync(
                    cancellationToken: processingCts.Token
                );
            }
            catch (InvalidOperationException)
            {
                await ShowWarningAsync("No transcription model is configured.");
                return;
            }

            await using var leaseScope = lease;
            var plugin = lease.Plugin;

            PublishStatus("Transcribing transform command...");
            var languageSelection = LanguageSelectionResolver.Resolve(
                _settings.Current.Language
            );
            string? command;
            try
            {
                var transcription = await plugin.TranscribeAsync(
                    wav,
                    languageSelection,
                    false,
                    null,
                    processingCts.Token
                );
                // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- Text comes from a plugin TranscribeAsync result; the non-null annotation is not guaranteed across plugin implementations
                command = transcription.Text?.Trim();
            }
            finally
            {
                // Native transcription is done — release _modelLock before the
                // LLM transform and text insertion below so a concurrent
                // dictation isn't blocked by them. The scope-end dispose is a
                // harmless idempotent no-op.
                // ReSharper disable once DisposeOnUsingVariable -- intentional early dispose to release the model lock before the LLM transform; the scope-end dispose is idempotent
                await leaseScope.DisposeAsync();
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                await ShowWarningAsync("The transform command returned no text.");
                return;
            }

            if (IsCancelCommand(command))
            {
                ShowFeedback("Transform canceled.", false);
                return;
            }

            PublishStatus($"Applying: {command}");
            var prompt = BuildTransformPrompt(session.SelectedText, command);
            var transformed = await _promptProcessing.ProcessSystemPromptAsync(
                prompt,
                session.SelectedText,
                ct: processingCts.Token
            );
            if (string.IsNullOrWhiteSpace(transformed))
            {
                await ShowWarningAsync("The transform result was empty.");
                return;
            }

            var replacementTarget = await ResolveReplacementTargetAsync(
                _activeWindow,
                session.TargetSnapshot,
                processingCts.Token
            );
            if (replacementTarget is null)
            {
                await AbortReplacementAsync(transformed);
                return;
            }

            PublishStatus("Replacing selected text...");
            var insertion = await DeliverValidatedTransformAsync(
                _textInsertion,
                transformed,
                replacementTarget,
                _commands.IsWaylandSession
            );

            switch (insertion)
            {
                case InsertionResult.CopiedToClipboard:
                    await ShowWarningAsync(
                        "Transformed text copied. Paste manually to replace the selection."
                    );
                    break;
                case InsertionResult.MissingClipboardTool:
                    await ShowWarningAsync(ClipboardToolMissingMessage());
                    break;
                case InsertionResult.MissingPasteTool:
                    await ShowWarningAsync(_commands.GetSnapshot().PasteToolInstallHint);
                    break;
                case not InsertionResult.Pasted and not InsertionResult.Typed:
                    await ShowWarningAsync("Could not insert transformed text.");
                    break;
                default:
                    ShowFeedback("Selection transformed.", false);
                    break;
            }
        }
        catch (Exception ex)
        {
            var deadlineExpired = processingCts.IsCancellationRequested;
            if (!deadlineExpired)
            {
                Trace.WriteLine($"[TransformSelection] Transform failed: {ex}");
            }

            await ShowWarningAsync(ProcessingFailureMessage(ex, deadlineExpired));
        }
    }

    internal static string ProcessingFailureMessage(Exception exception, bool deadlineExpired)
    {
        return deadlineExpired
            ? "Transform selection timed out."
            : $"Transform selection failed: {LanguageSelectionUiMessage.From(exception)}";
    }

    private async Task AbortReplacementAsync(string transformed)
    {
        var insertion = await DeliverAbortedTransformAsync(_textInsertion, transformed);
        var message = insertion switch
        {
            InsertionResult.CopiedToClipboard =>
                "Focus changed while transforming — the original selection was left alone. "
                + "Transformed text copied; paste manually to replace it.",
            InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
            _ =>
                "Focus changed while transforming, and the transformed text could not be copied. "
                + "The original selection was left alone.",
        };
        await ShowWarningAsync(message);
    }

    private async Task ShowWarningAsync(string message)
    {
        ShowFeedback(message, true);
        await _showWarningDialog(message);
    }

    private static async Task ShowWarningDialogAsync(string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new MessageDialogWindow();
            await dialog.ShowMessageAsync("TypeWhisper", message);
        });
    }

    private void PublishStatus(string message)
    {
        PublishOverlay(state =>
            state with
            {
                IsOverlayVisible = true,
                StatusText = message,
                ShowFeedback = false,
                FeedbackText = null,
                IsRecording = false,
                SessionStartedAtUtc = null,
            }
        );
    }

    private void ShowFeedback(string message, bool isError)
    {
        PublishOverlay(_ => new DictationOverlayState
        {
            IsOverlayVisible = false,
            ShowFeedback = true,
            FeedbackIsError = isError,
            FeedbackText = message,
            StatusText = message,
        });
    }

    private static string? ResolveActiveAppName(ActiveWindowSnapshot? snapshot)
    {
        return string.IsNullOrWhiteSpace(snapshot?.ProcessName)
            ? snapshot?.Title
            : snapshot.ProcessName;
    }

    private static string ClipboardToolMissingMessage()
    {
        return WaylandSessionDetector.HasWaylandDisplay()
            ? "Install wl-clipboard to copy transformed text."
            : "Install xclip to copy transformed text.";
    }

    private void PublishOverlay(Func<DictationOverlayState, DictationOverlayState> updater)
    {
        var token = _overlayToken
                    ?? throw new InvalidOperationException(
                        "Transform overlay publication requires an active presentation token."
                    );
        var state = updater(_overlayState);
        if (!_overlayCoordinator.Update(token, _ => state))
        {
            return;
        }

        _overlayState = state;
        OverlayStateChanged?.Invoke(this, _overlayState);
    }

    internal sealed record TransformSelectionCapture(
        string SelectedText,
        ActiveWindowSnapshot? TargetSnapshot
    );

    private sealed record TransformSelectionSession(
        string SelectedText,
        ActiveWindowSnapshot? TargetSnapshot,
        AudioRecordingService.AudioCaptureSession CaptureSession
    );
}
