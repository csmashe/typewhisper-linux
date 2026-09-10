using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Applies rule-based and optional LLM cleanup to transcribed text.
///     Light cleanup is always local (no LLM). Medium / High first run the
///     local pass, then send the lightened text to the configured LLM
///     provider — falling back to Light if no provider is available.
/// </summary>
public sealed class LlmCleanupService
{
    private readonly CleanupService _cleanup;
    private readonly PromptProcessingService _promptProcessing;
    private readonly IErrorLogService? _errorLog;

    // Last cleanup failure written to the error log. A broken provider fails on every dictation,
    // so only a change of message is worth a new entry.
    private string? _lastLoggedFailure;

    public LlmCleanupService(
        CleanupService cleanup,
        PromptProcessingService promptProcessing,
        IErrorLogService? errorLog = null
    )
    {
        _cleanup = cleanup;
        _promptProcessing = promptProcessing;
        _errorLog = errorLog;
    }

    public async Task<string> CleanAsync(
        string text,
        CleanupLevel level,
        Func<string, Task>? statusCallback = null,
        LlmCallCapture? capture = null,
        CancellationToken ct = default
    )
    {
        // Medium/High intentionally fall through to the LLM cleanup path below.
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (level)
        {
            case CleanupLevel.None:
                return text;
            case CleanupLevel.Light:
                return _cleanup.Clean(text, CleanupLevel.Light);
        }

        var lightText = _cleanup.Clean(text, CleanupLevel.Light);
        if (_promptProcessing.HasNoProviderForRequest())
        {
            await NotifyStatusAsync(
                statusCallback,
                "Cleanup provider unavailable. Using Light cleanup."
            );
            return lightText;
        }

        await NotifyStatusAsync(
            statusCallback,
            level == CleanupLevel.Medium ? "Applying Medium cleanup..." : "Applying High cleanup..."
        );

        try
        {
            var prompt = CleanupService.GetLlmSystemPrompt(level);
            var cleaned = await _promptProcessing.ProcessSystemPromptAsync(
                prompt,
                lightText,
                capture,
                ct
            );
            // Cleanup works again, so the same failure recurring later is worth logging afresh.
            _lastLoggedFailure = null;
            return string.IsNullOrWhiteSpace(cleaned) ? lightText : cleaned.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LlmCleanupService] Cleanup failed: {ex.Message}");
            // User-actionable: the requested Medium/High cleanup silently degraded to
            // Light because the LLM call failed (key, network, provider outage).
            var failure = $"AI cleanup failed and fell back to Light cleanup: {ex.Message}";
            // A configuration failure is already one entry from PromptProcessingService; wrapping it
            // here would describe the same event twice.
            var alreadyLogged =
                ex is PluginRequestException { FailureKind: PluginRequestFailureKind.Configuration };
            if (!alreadyLogged
                && !string.Equals(failure, _lastLoggedFailure, StringComparison.Ordinal))
            {
                _lastLoggedFailure = failure;
                _errorLog?.AddEntry(failure, ErrorCategory.Prompt);
            }

            await NotifyStatusAsync(statusCallback, "Cleanup failed. Using Light cleanup.");
            return lightText;
        }
    }

    private static async Task NotifyStatusAsync(Func<string, Task>? statusCallback, string message)
    {
        if (statusCallback is null)
        {
            return;
        }

        try
        {
            await statusCallback(message);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LlmCleanupService] Status callback failed: {ex.Message}");
        }
    }
}
