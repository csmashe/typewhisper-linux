using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Applies rule-based and optional LLM cleanup to transcribed text.
///     Light cleanup is always local (no LLM). Medium / High first run the
///     local pass, then send the lightened text to the configured LLM
///     provider, falling back to Light when unavailable, failed or over the wait limit.
/// </summary>
public sealed class LlmCleanupService
{
    private readonly CleanupService _cleanup;
    private readonly PromptProcessingService _promptProcessing;
    private readonly IErrorLogService? _errorLog;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

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
        TimeSpan? timeout = null,
        CancellationToken ct = default,
        string? sourceLanguage = null,
        string? providerOverride = null,
        bool preparedLightText = false
    )
    {
        ct.ThrowIfCancellationRequested();
        // Medium/High intentionally fall through to the LLM cleanup path below.
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (level)
        {
            case CleanupLevel.None:
                return text;
            case CleanupLevel.Light:
                return _cleanup.Clean(text, CleanupLevel.Light, sourceLanguage);
        }

        var lightText = preparedLightText ? text : PrepareLiveInput(text, sourceLanguage);
        if (_promptProcessing.HasNoProviderForRequest(providerOverride))
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
            var cleaned = await ProcessWithinBudgetAsync(
                prompt,
                lightText,
                capture,
                timeout ?? TimeSpan.FromSeconds(5),
                ct,
                providerOverride
            );
            ct.ThrowIfCancellationRequested();
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
        catch (CleanupDeadlineExceededException)
        {
            const string failure = "AI cleanup exceeded its wait limit and fell back to Light cleanup.";
            if (!string.Equals(failure, _lastLoggedFailure, StringComparison.Ordinal))
            {
                _lastLoggedFailure = failure;
                _errorLog?.AddEntry(failure, ErrorCategory.Prompt);
            }
            await NotifyStatusAsync(statusCallback, Loc.Instance["Dictation.CleanupTimedOut"]);
            ct.ThrowIfCancellationRequested();
            return lightText;
        }
        catch (Exception ex)
        {
            var safeMessage =
                FailureMessageSanitizer.Sanitize(ex.Message, text, lightText, CleanupService.GetLlmSystemPrompt(level))
                ?? Loc.Instance["Common.UnknownError"];
            Trace.WriteLine($"[LlmCleanupService] Cleanup failed: {safeMessage}");
            // User-actionable: the requested Medium/High cleanup silently degraded to
            // Light because the LLM call failed (key, network, provider outage).
            var failure = $"AI cleanup failed and fell back to Light cleanup: {safeMessage}";
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

    internal string PrepareLiveInput(string text, string? language) =>
        _cleanup.Clean(text, CleanupLevel.Light, language);

    // Bound the caller's wait even if a plugin ignores cancellation. The gate stays held
    // until the provider settles, so later dictations cannot accumulate abandoned requests.
    private async Task<string> ProcessWithinBudgetAsync(
        string prompt, string text, LlmCallCapture? capture, TimeSpan timeout, CancellationToken ct,
        string? providerOverride)
    {
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timerCts = new CancellationTokenSource();
        var completed = false;
        var request = Task.Run(async () =>
        {
            await _requestGate.WaitAsync(requestCts.Token).ConfigureAwait(false);
            try
            {
                requestCts.Token.ThrowIfCancellationRequested();
                return await _promptProcessing.ProcessSystemPromptAsync(
                    prompt, text, capture, requestCts.Token, providerOverride).ConfigureAwait(false);
            }
            finally
            {
                _requestGate.Release();
            }
        });
        try
        {
            var deadline = Task.Delay(timeout > TimeSpan.Zero ? timeout : Timeout.InfiniteTimeSpan, timerCts.Token);
            var winner = await Task.WhenAny(request, deadline).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (winner != request)
                throw new CleanupDeadlineExceededException();
            var result = await request.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            completed = true;
            return result;
        }
        finally
        {
            await timerCts.CancelAsync().ConfigureAwait(false);
            var cancellation = completed ? Task.CompletedTask : requestCts.CancelAsync();
            // A cancellation callback can also stall. Observe it and dispose the source only
            // after both tasks finish; never let late provider work mutate an inserted result.
            _ = Task.WhenAll(request, cancellation).ContinueWith(
                task => { _ = task.Exception; requestCts.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed class CleanupDeadlineExceededException : TimeoutException;

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
