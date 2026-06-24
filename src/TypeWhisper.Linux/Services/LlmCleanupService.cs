using System.Diagnostics;
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

    public LlmCleanupService(CleanupService cleanup, PromptProcessingService promptProcessing)
    {
        _cleanup = cleanup;
        _promptProcessing = promptProcessing;
    }

    public async Task<string> CleanAsync(
        string text,
        CleanupLevel level,
        Func<string, Task>? statusCallback = null,
        CancellationToken ct = default
    )
    {
        if (level == CleanupLevel.None)
        {
            return text;
        }

        if (level == CleanupLevel.Light)
        {
            return CleanupService.Clean(text, CleanupLevel.Light);
        }

        var lightText = CleanupService.Clean(text, CleanupLevel.Light);
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
            var cleaned = await _promptProcessing.ProcessSystemPromptAsync(prompt, lightText, ct);
            return string.IsNullOrWhiteSpace(cleaned) ? lightText : cleaned.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LlmCleanupService] Cleanup failed: {ex.Message}");
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