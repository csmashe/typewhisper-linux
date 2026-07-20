using Avalonia.Threading;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Services;

public sealed class TransformSelectionService
{
    private static readonly TimeSpan s_processingTimeout = TimeSpan.FromSeconds(90);
    private readonly ActiveWindowService _activeWindow;
    private readonly AudioRecordingService _audio;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ModelManagerService _models;
    private readonly PromptProcessingService _promptProcessing;
    private readonly ISettingsService _settings;

    private readonly TextInsertionService _textInsertion;
    private DictationOverlayState _overlayState = DictationOverlayState.Hidden;

    private TransformSelectionSession? _session;

    public TransformSelectionService(
        TextInsertionService textInsertion,
        AudioRecordingService audio,
        ModelManagerService models,
        PromptProcessingService promptProcessing,
        ISettingsService settings,
        ActiveWindowService activeWindow,
        SystemCommandAvailabilityService commands
    )
    {
        _textInsertion = textInsertion;
        _audio = audio;
        _models = models;
        _promptProcessing = promptProcessing;
        _settings = settings;
        _activeWindow = activeWindow;
        _commands = commands;
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
    internal static Task<string> CaptureSelectionForTransformAsync(
        TextInsertionService textInsertion,
        string? processName
    )
    {
        return textInsertion.CaptureSelectedTextAsync(
            TextInsertionService.IsTerminalApp(processName)
        );
    }

    // Test seam: decides whether it's still safe to replace the captured selection. See audit §3 M6.
    // Cannot detect caret/selection drift within the same window — that would need an
    // AT-SPI selection-range or document-revision token.
    internal static bool HasSelectionTargetChanged(
        string? capturedWindowId,
        string? capturedProcessName,
        string? currentWindowId,
        string? currentProcessName
    )
    {
        // Window id is the strongest signal (X11 only) — if both sides have one, trust it
        // even if process-name detection disagrees.
        if (!string.IsNullOrEmpty(capturedWindowId) && !string.IsNullOrEmpty(currentWindowId))
        {
            return !string.Equals(capturedWindowId, currentWindowId, StringComparison.Ordinal);
        }

        // Wayland (and any X11 case missing an id on one side) falls back to process
        // identity — the only cross-compositor signal ActiveWindowService exposes.
        if (!string.IsNullOrEmpty(capturedProcessName) || !string.IsNullOrEmpty(currentProcessName))
        {
            return !string.Equals(
                capturedProcessName,
                currentProcessName,
                StringComparison.OrdinalIgnoreCase
            );
        }

        // Neither side offered a process name. A window id on exactly one side means identity
        // appeared or vanished between capture and replace — usually the captured window
        // closing — so treat it as changed rather than replacing into an unconfirmable window.
        if (!string.IsNullOrEmpty(capturedWindowId) || !string.IsNullOrEmpty(currentWindowId))
        {
            return true;
        }

        // No identity signal on either side — fail open rather than block a replacement we can't validate.
        return false;
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

        var windowId = _activeWindow.GetActiveWindowId();
        var processName = _activeWindow.GetActiveWindowProcessName();
        var windowTitle = _activeWindow.GetActiveWindowTitle();
        var selectedText = await CaptureSelectionForTransformAsync(_textInsertion, processName);
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
            windowId,
            processName,
            windowTitle,
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
                ActiveAppName = string.IsNullOrWhiteSpace(processName) ? windowTitle : processName,
                SessionStartedAtUtc = DateTime.UtcNow
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
                ActiveAppName = string.IsNullOrWhiteSpace(session.ProcessName)
                    ? session.WindowTitle
                    : session.ProcessName,
                SessionStartedAtUtc = null
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

        try
        {
            using var cts = new CancellationTokenSource(s_processingTimeout);
            ModelManagerService.TranscriptionLease lease;
            try
            {
                lease = await _models.AcquireTranscriptionAsync(cancellationToken: cts.Token);
            }
            catch (InvalidOperationException)
            {
                await ShowWarningAsync("No transcription model is configured.");
                return;
            }

            await using var leaseScope = lease;
            var plugin = lease.Plugin;

            PublishStatus("Transcribing transform command...");
            var language =
                _settings.Current.Language is { Length: > 0 } lang && lang != "auto" ? lang : null;
            string? command;
            try
            {
                var transcription = await plugin.TranscribeAsync(
                    wav,
                    language,
                    false,
                    null,
                    cts.Token
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
                ct: cts.Token
            );
            if (string.IsNullOrWhiteSpace(transformed))
            {
                await ShowWarningAsync("The transform result was empty.");
                return;
            }

            var currentWindowId = _activeWindow.GetActiveWindowId();
            var currentProcessName = _activeWindow.GetActiveWindowProcessName();
            if (
                HasSelectionTargetChanged(
                    session.WindowId,
                    session.ProcessName,
                    currentWindowId,
                    currentProcessName
                )
            )
            {
                await AbortReplacementAsync(transformed);
                return;
            }

            PublishStatus("Replacing selected text...");
            var insertion = await _textInsertion.InsertTextAsync(
                transformed,
                true,
                session.WindowId,
                session.ProcessName,
                session.WindowTitle
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
        catch (OperationCanceledException)
        {
            await ShowWarningAsync("Transform selection timed out.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TransformSelection] Transform failed: {ex}");
            await ShowWarningAsync($"Transform selection failed: {ex.Message}");
        }
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
                + "The original selection was left alone."
        };
        await ShowWarningAsync(message);
    }

    private async Task ShowWarningAsync(string message)
    {
        ShowFeedback(message, true);
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
                SessionStartedAtUtc = null
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
            StatusText = message
        });
    }

    private static string ClipboardToolMissingMessage()
    {
        return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Install wl-clipboard to copy transformed text."
            : "Install xclip to copy transformed text.";
    }

    private void PublishOverlay(Func<DictationOverlayState, DictationOverlayState> updater)
    {
        _overlayState = updater(_overlayState);
        OverlayStateChanged?.Invoke(this, _overlayState);
    }

    private sealed record TransformSelectionSession(
        string SelectedText,
        string? WindowId,
        string? ProcessName,
        string? WindowTitle,
        AudioRecordingService.AudioCaptureSession CaptureSession
    );
}
