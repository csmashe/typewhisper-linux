using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

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
        CancellationToken ct = default)
    {
        if (level == CleanupLevel.None)
            return text;

        if (level == CleanupLevel.Light)
            return _cleanup.Clean(text, CleanupLevel.Light);

        var lightText = _cleanup.Clean(text, CleanupLevel.Light);
        if (!_promptProcessing.IsAnyProviderAvailable)
        {
            if (statusCallback is not null)
                await statusCallback("Cleanup provider unavailable. Using Light cleanup.");
            return lightText;
        }

        if (statusCallback is not null)
        {
            await statusCallback(level == CleanupLevel.Medium
                ? "Applying Medium cleanup..."
                : "Applying High cleanup...");
        }

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
        catch
        {
            if (statusCallback is not null)
                await statusCallback("Cleanup failed. Using Light cleanup.");
            return lightText;
        }
    }
}
