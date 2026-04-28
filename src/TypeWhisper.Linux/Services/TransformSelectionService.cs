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

    private async Task StartAsync()
    {
        if (!_promptProcessing.IsAnyProviderAvailable)
        {
            await ShowWarningAsync("TypeWhisper", "No LLM provider available. Please configure an API key in Plugins.");
            return;
        }

        var windowId = _activeWindow.GetActiveWindowId();
        var processName = _activeWindow.GetActiveWindowProcessName();
        var windowTitle = _activeWindow.GetActiveWindowTitle();
        var selectedText = await _textInsertion.CaptureSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            await ShowWarningAsync("TypeWhisper", "Select text before using Transform Selection.");
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
            await ShowWarningAsync("TypeWhisper", $"Could not start recording: {ex.Message}");
            return;
        }

        if (!_audio.IsRecording)
        {
            await ShowWarningAsync("TypeWhisper", "Could not start recording. Check your microphone settings.");
            return;
        }

        _session = new TransformSelectionSession(selectedText, windowId, processName, windowTitle);
    }

    private async Task StopAndTransformAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
            return;

        byte[] wav;
        try
        {
            wav = await _audio.StopRecordingAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TransformSelection] Failed to stop recording: {ex}");
            await ShowWarningAsync("TypeWhisper", $"Could not stop recording: {ex.Message}");
            return;
        }

        if (wav.Length == 0)
        {
            await ShowWarningAsync("TypeWhisper", "No command audio was recorded.");
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(ProcessingTimeout);
            if (!await _models.EnsureModelLoadedAsync(cancellationToken: cts.Token))
            {
                await ShowWarningAsync("TypeWhisper", "No transcription model is configured.");
                return;
            }

            var plugin = _models.ActiveTranscriptionPlugin;
            if (plugin is null)
            {
                await ShowWarningAsync("TypeWhisper", "No transcription model is loaded.");
                return;
            }

            var language = _settings.Current.Language is { Length: > 0 } lang && lang != "auto" ? lang : null;
            var transcription = await plugin.TranscribeAsync(wav, language, translate: false, prompt: null, ct: cts.Token);
            var command = transcription.Text?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                await ShowWarningAsync("TypeWhisper", "The transform command returned no text.");
                return;
            }

            var prompt = BuildTransformPrompt(session.SelectedText, command);
            var transformed = await _promptProcessing.ProcessSystemPromptAsync(prompt, session.SelectedText, cts.Token);
            if (string.IsNullOrWhiteSpace(transformed))
            {
                await ShowWarningAsync("TypeWhisper", "The transform result was empty.");
                return;
            }

            var insertion = await _textInsertion.InsertTextAsync(
                transformed,
                autoPaste: true,
                targetWindowId: session.WindowId,
                targetProcessName: session.ProcessName,
                targetWindowTitle: session.WindowTitle);

            if (insertion is InsertionResult.CopiedToClipboard)
                await ShowWarningAsync("TypeWhisper", "Transformed text copied. Paste manually to replace the selection.");
            else if (insertion is not InsertionResult.Pasted and not InsertionResult.Typed)
                await ShowWarningAsync("TypeWhisper", "Could not insert transformed text.");
        }
        catch (OperationCanceledException)
        {
            await ShowWarningAsync("TypeWhisper", "Transform selection timed out.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TransformSelection] Transform failed: {ex}");
            await ShowWarningAsync("TypeWhisper", $"Transform selection failed: {ex.Message}");
        }
    }

    private static async Task ShowWarningAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new MessageDialogWindow();
            await dialog.ShowMessageAsync(title, message);
        });
    }

    private sealed record TransformSelectionSession(
        string SelectedText,
        string? WindowId,
        string? ProcessName,
        string? WindowTitle);
}
