using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Views;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public sealed class PromptPaletteService
{
    private readonly ActiveWindowService _activeWindow;
    private readonly PluginManager _pluginManager;
    private readonly PromptProcessingService _processing;
    private readonly IPromptActionService _promptActions;
    private readonly IServiceProvider _services;
    private readonly TextInsertionService _textInsertion;

    private bool _opening;

    public PromptPaletteService(
        IServiceProvider services,
        IPromptActionService promptActions,
        PromptProcessingService processing,
        TextInsertionService textInsertion,
        PluginManager pluginManager,
        ActiveWindowService activeWindow
    )
    {
        _services = services;
        _promptActions = promptActions;
        _processing = processing;
        _textInsertion = textInsertion;
        _pluginManager = pluginManager;
        _activeWindow = activeWindow;
    }

    public async Task TogglePaletteAsync()
    {
        if (_opening)
        {
            return;
        }

        _opening = true;
        try
        {
            await OpenPaletteAsync();
        }
        finally
        {
            _opening = false;
        }
    }

    private async Task OpenPaletteAsync()
    {
        var actions = _promptActions.EnabledActions;
        if (actions.Count == 0)
        {
            return;
        }

        // Capture the target window now, while the user's editor is still focused
        // — the palette is about to steal focus. The paste re-activates this window
        // before inserting (FocusTargetWindowAsync), so the result lands back in
        // the editor even though the palette held focus while streaming.
        var targetWindowId = _activeWindow.GetActiveWindowId();

        var capturedText = await _textInsertion.CaptureSelectedTextAsync();

        PromptAction? selectedAction = null;
        PromptPaletteWindow? window = null;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            window = _services.GetRequiredService<PromptPaletteWindow>();
            window.SourceText = capturedText;
            window.SetActions(actions);
            selectedAction = await window.ShowAndWaitAsync();
        });

        // On a null pick the window already closed itself (Complete(null)). On a
        // real pick it stays open in its running state so the result can stream
        // into it; ExecuteActionAsync owns closing it from here on.
        if (selectedAction is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(capturedText))
        {
            await CloseWindowAsync(window);
            return;
        }

        await ExecuteActionAsync(selectedAction, capturedText, window, targetWindowId);
    }

    /// <summary>
    ///     Direct-hotkey entry point (B12): looks up the action by ID, captures
    ///     the current selection, runs the action against it. No palette is
    ///     opened. Manual-only actions are intentionally NOT filtered here —
    ///     that's the whole point of the B13 flag (hide from auto pipeline,
    ///     keep direct-hotkey + palette execution). Disabled actions are
    ///     filtered by <see cref="IPromptActionService.EnabledActions" /> so a
    ///     stale hotkey on a disabled action is a no-op.
    /// </summary>
    public async Task ExecuteActionDirectAsync(string actionId)
    {
        var action = _promptActions.EnabledActions.FirstOrDefault(a => a.Id == actionId);
        if (action is null)
        {
            return;
        }

        var targetWindowId = _activeWindow.GetActiveWindowId();

        var captured = await _textInsertion.CaptureSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(captured))
        {
            return;
        }

        // Direct-hotkey path: no palette window, so streaming has no UI sink
        // (window: null → no-op renders). The result still streams + falls back
        // to batch and is pasted exactly as before.
        await ExecuteActionAsync(action, captured, window: null, targetWindowId);
    }

    private async Task ExecuteActionAsync(
        PromptAction action,
        string capturedText,
        PromptPaletteWindow? window,
        string? targetWindowId
    )
    {
        if (!_processing.IsAnyProviderAvailable)
        {
            await CloseWindowAsync(window);
            await ShowWarningAsync(
                "TypeWhisper",
                "No LLM provider available. Please configure an API key in Plugins."
            );
            return;
        }

        // userCts is tripped only by a genuine user abort — closing the palette or
        // pressing Escape while it runs (wired via AttachRunCancellation). It is
        // kept separate from the per-attempt timeout budgets so that a user abort
        // skips both insertion and the batch fallback, while a streaming timeout /
        // stall (no user action) still falls back to batch.
        using var userCts = new CancellationTokenSource();
        if (window is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.AttachRunCancellation(userCts));
        }

        try
        {
            var result = await RunActionAsync(action, capturedText, window, userCts.Token);

            // If the user aborted while the result was being finalized, do not
            // paste a result they tried to cancel.
            userCts.Token.ThrowIfCancellationRequested();

            // Close the palette BEFORE inserting so focus returns to the target
            // app and the paste lands where the user selected the text.
            await CloseWindowAsync(window);

            var actionPlugin = ResolveActionPlugin(action);
            if (actionPlugin is not null)
            {
                var context = new ActionContext(null, null, null, null, capturedText);
                using var execCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await actionPlugin.ExecuteAsync(result, context, execCts.Token);
                return;
            }

            await _textInsertion.InsertTextAsync(result, targetWindowId: targetWindowId);
        }
        catch (OperationCanceledException)
        {
            // User abort or everything timed out — best effort, no insertion.
            await CloseWindowAsync(window);
        }
        catch (Exception ex)
        {
            await CloseWindowAsync(window);
            Debug.WriteLine($"[PromptPalette] Prompt processing failed: {ex}");
            await ShowWarningAsync("TypeWhisper", $"Prompt processing failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Runs the prompt action, streaming into the palette when present, and
    ///     returns the final text. A user abort (<paramref name="userToken" />)
    ///     propagates as <see cref="OperationCanceledException" /> with no fallback;
    ///     a streaming fault, an empty stream, or a streaming-attempt timeout that
    ///     the user did NOT trigger falls back to a fresh batch request — so a
    ///     provider whose streaming path stalls or breaks still degrades to the
    ///     known-good batch path (keeps the default-on toggle safe).
    /// </summary>
    private async Task<string> RunActionAsync(
        PromptAction action,
        string capturedText,
        PromptPaletteWindow? window,
        CancellationToken userToken
    )
    {
        // Stream the response into the palette's result area at ~30 Hz; the pump
        // marshals each coalesced flush onto the UI thread.
        var pump = new LlmStreamPump(accumulated =>
            Dispatcher.UIThread.Post(() => window?.UpdateResult(accumulated))
        );

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        streamCts.CancelAfter(TimeSpan.FromSeconds(60));

        string streamed;
        try
        {
            streamed = await pump.RunAsync(
                _processing.ProcessStreamingAsync(action, capturedText, streamCts.Token),
                streamCts.Token
            );
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw; // genuine user abort — do not fall back, do not insert.
        }
        catch (OperationCanceledException)
        {
            // Streaming stalled until the attempt timeout but the user did not
            // cancel: treat it as a recoverable streaming failure and fall back.
            return await BatchAsync(action, capturedText, userToken);
        }

        // Streaming→batch lossless fallback (mirrors DictationOrchestrator): retry
        // once with the known-good batch path when the pump faulted OR the stream
        // yielded nothing at all. A legitimately empty result delivered as a single
        // chunk (toggle-off / bulk-yield) sets ReceivedAnyChunk, so it is NOT re-run.
        return pump.Faulted || !pump.ReceivedAnyChunk
            ? await BatchAsync(action, capturedText, userToken)
            : streamed;
    }

    private async Task<string> BatchAsync(
        PromptAction action,
        string capturedText,
        CancellationToken userToken
    )
    {
        // Fresh timeout budget so a streaming attempt that burned its budget does
        // not leave the batch fallback with no time to succeed.
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        batchCts.CancelAfter(TimeSpan.FromSeconds(60));
        return await _processing.ProcessAsync(action, capturedText, batchCts.Token);
    }

    private static async Task CloseWindowAsync(PromptPaletteWindow? window)
    {
        if (window is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(window.ClosePalette);
    }

    private IActionPlugin? ResolveActionPlugin(PromptAction action)
    {
        if (string.IsNullOrWhiteSpace(action.TargetActionPluginId))
        {
            return null;
        }

        return _pluginManager.ActionPlugins.FirstOrDefault(plugin =>
            string.Equals(
                plugin.PluginId,
                action.TargetActionPluginId,
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                plugin.ActionId,
                action.TargetActionPluginId,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    private static async Task ShowWarningAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new MessageDialogWindow();
            await dialog.ShowMessageAsync(title, message);
        });
    }
}
