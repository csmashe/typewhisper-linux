using System.Diagnostics;
using Avalonia.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Services;

public sealed class TransformSelectionService
{
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(90);

    private readonly TextInsertionService _textInsertion;
    private readonly AudioRecordingService _audio;
    private readonly ModelManagerService _models;
    private readonly PromptProcessingService _promptProcessing;
    private readonly ISettingsService _settings;
    private readonly ActiveWindowService _activeWindow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TransformSelectionSession? _session;
    private DictationOverlayState _overlayState = DictationOverlayState.Hidden;

    public event EventHandler<DictationOverlayState>? OverlayStateChanged;

    public TransformSelectionService(
        TextInsertionService textInsertion,
        AudioRecordingService audio,
        ModelManagerService models,
        PromptProcessingService promptProcessing,
        ISettingsService settings,
        ActiveWindowService activeWindow)
    {
        _textInsertion = textInsertion;
        _audio = audio;
        _models = models;
        _promptProcessing = promptProcessing;
        _settings = settings;
        _activeWindow = activeWindow;
    }

    public async Task ToggleAsync()
    {
        if (!await _gate.WaitAsync(0))
            return;

        try
        {
            if (_session is null)
                await StartAsync();
            else
                await StopAndTransformAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string BuildTransformPrompt(string selectedText, string command) =>
        $"""
        You transform selected text based on a spoken command.
        Return only the transformed text.
        Preserve meaning unless the command asks otherwise.

        Selected text:
        {selectedText}

        Command:
        {command}
        """;

    internal static bool IsCancelCommand(string command)
    {
        var normalized = command.Trim().Trim('.', '!', '?', ',');
        return normalized.Equals("cancel", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("cancel that", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("never mind", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("nevermind", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("stop", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartAsync()
    {
        if (!_promptProcessing.IsAnyProviderAvailable)
        {
            await ShowWarningAsync("No LLM provider available. Please configure an API key in Plugins.");
            return;
        }

        var windowId = _activeWindow.GetActiveWindowId();
        var processName = _activeWindow.GetActiveWindowProcessName();
        var windowTitle = _activeWindow.GetActiveWindowTitle();
        var selectedText = await _textInsertion.CaptureSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            await ShowWarningAsync("Select text before using Transform Selection.");
            return;
        }

        _audio.WhisperModeEnabled = _settings.Current.WhisperModeEnabled;
        try
        {
            _audio.StartRecording();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TransformSelection] Failed to start recording: {ex}");
            await ShowWarningAsync($"Could not start recording: {ex.Message}");
            return;
        }

        if (!_audio.IsRecording)
        {
            await ShowWarningAsync("Could not start recording. Check your microphone settings.");
            return;
        }

        _session = new TransformSelectionSession(selectedText, windowId, processName, windowTitle);
        PublishOverlay(state => state with
        {
            IsOverlayVisible = true,
            ShowFeedback = false,
            FeedbackText = null,
            FeedbackIsError = false,
            IsRecording = true,
            StatusText = "Transform command: speak the edit, then press the hotkey again.",
            PartialText = selectedText,
            ActiveAppName = string.IsNullOrWhiteSpace(processName) ? windowTitle : processName,
            SessionStartedAtUtc = DateTime.UtcNow
        });
    }

    private async Task StopAndTransformAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
            return;

        PublishOverlay(state => state with
        {
            IsOverlayVisible = true,
            ShowFeedback = false,
            FeedbackText = null,
            IsRecording = false,
            StatusText = "Processing transform command...",
            PartialText = session.SelectedText,
            ActiveAppName = string.IsNullOrWhiteSpace(session.ProcessName) ? session.WindowTitle : session.ProcessName,
            SessionStartedAtUtc = null
        });

        byte[] wav;
        try
        {
            wav = await _audio.StopRecordingAsync();
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
            using var cts = new CancellationTokenSource(ProcessingTimeout);
            if (!await _models.EnsureModelLoadedAsync(cancellationToken: cts.Token))
            {
                await ShowWarningAsync("No transcription model is configured.");
                return;
            }

            var plugin = _models.ActiveTranscriptionPlugin;
            if (plugin is null)
            {
                await ShowWarningAsync("No transcription model is loaded.");
                return;
            }

            PublishStatus("Transcribing transform command...");
            var language = _settings.Current.Language is { Length: > 0 } lang && lang != "auto" ? lang : null;
            var transcription = await plugin.TranscribeAsync(wav, language, translate: false, prompt: null, ct: cts.Token);
            var command = transcription.Text?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                await ShowWarningAsync("The transform command returned no text.");
                return;
            }

            if (IsCancelCommand(command))
            {
                ShowFeedback("Transform canceled.", isError: false);
                return;
            }

            PublishStatus($"Applying: {command}");
            var prompt = BuildTransformPrompt(session.SelectedText, command);
            var transformed = await _promptProcessing.ProcessSystemPromptAsync(prompt, session.SelectedText, cts.Token);
            if (string.IsNullOrWhiteSpace(transformed))
            {
                await ShowWarningAsync("The transform result was empty.");
                return;
            }

            PublishStatus("Replacing selected text...");
            var insertion = await _textInsertion.InsertTextAsync(
                transformed,
                autoPaste: true,
                targetWindowId: session.WindowId,
                targetProcessName: session.ProcessName,
                targetWindowTitle: session.WindowTitle);

            if (insertion is InsertionResult.CopiedToClipboard)
                await ShowWarningAsync("Transformed text copied. Paste manually to replace the selection.");
            else if (insertion is InsertionResult.MissingClipboardTool)
                await ShowWarningAsync(ClipboardToolMissingMessage());
            else if (insertion is InsertionResult.MissingPasteTool)
                await ShowWarningAsync("Install xdotool to paste transformed text automatically.");
            else if (insertion is not InsertionResult.Pasted and not InsertionResult.Typed)
                await ShowWarningAsync("Could not insert transformed text.");
            else
                ShowFeedback("Selection transformed.", isError: false);
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

    private async Task ShowWarningAsync(string message)
    {
        ShowFeedback(message, isError: true);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new MessageDialogWindow();
            await dialog.ShowMessageAsync("TypeWhisper", message);
        });
    }

    private void PublishStatus(string message) => PublishOverlay(state => state with
    {
        IsOverlayVisible = true,
        StatusText = message,
        ShowFeedback = false,
        FeedbackText = null,
        IsRecording = false,
        SessionStartedAtUtc = null
    });

    private void ShowFeedback(string message, bool isError) => PublishOverlay(_ => new DictationOverlayState
    {
        IsOverlayVisible = false,
        ShowFeedback = true,
        FeedbackIsError = isError,
        FeedbackText = message,
        StatusText = message
    });

    private static string ClipboardToolMissingMessage() =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Install wl-clipboard to copy transformed text."
            : "Install xclip to copy transformed text.";

    private void PublishOverlay(Func<DictationOverlayState, DictationOverlayState> updater)
    {
        _overlayState = updater(_overlayState);
        OverlayStateChanged?.Invoke(this, _overlayState);
    }

    private sealed record TransformSelectionSession(
        string SelectedText,
        string? WindowId,
        string? ProcessName,
        string? WindowTitle);
}
