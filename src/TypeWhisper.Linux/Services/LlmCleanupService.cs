using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

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
        string? referenceContext = null,
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
        if (!_promptProcessing.IsAnyProviderAvailable)
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
                ct,
                referenceContext
            );
            return string.IsNullOrWhiteSpace(cleaned) ? lightText : cleaned.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LlmCleanupService] Cleanup failed: {ex.Message}");
            // User-actionable: the requested Medium/High cleanup silently degraded to
            // Light because the LLM call failed (key, network, provider outage).
            _errorLog?.AddEntry(
                $"AI cleanup failed and fell back to Light cleanup: {ex.Message}",
                ErrorCategory.Prompt
            );
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