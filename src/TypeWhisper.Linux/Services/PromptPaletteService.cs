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

    /// <summary>
    ///     Direct-hotkey entry point: runs the action by ID on the current selection
    ///     without opening the palette. Manual-only actions are not filtered here
    ///     (that's the B13 flag's purpose). Disabled actions are filtered by
    ///     <see cref="IPromptActionService.EnabledActions" /> so stale hotkeys are no-ops.
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

        // No palette window: streaming has no UI sink (null → no-op renders),
        // but the result still streams + falls back to batch and is pasted normally.
        await ExecuteActionAsync(action, captured, null, targetWindowId);
    }

    private async Task OpenPaletteAsync()
    {
        var actions = _promptActions.EnabledActions;
        if (actions.Count == 0)
        {
            return;
        }

        // Capture the target window before the palette steals focus, so the paste
        // can re-activate it and land in the editor even while the palette streamed.
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

        // Null pick = window already closed itself. A real pick leaves it open
        // in running state so the result can stream in; ExecuteActionAsync owns closing it.
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

        // userCts is tripped only by a genuine user abort (Escape/palette close).
        // Kept separate from timeout budgets so an abort skips insertion AND batch
        // fallback, while a streaming stall (no user action) still falls back to batch.
        using var userCts = new CancellationTokenSource();
        if (window is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.AttachRunCancellation(userCts));
        }

        try
        {
            var result = await RunActionAsync(action, capturedText, window, userCts.Token);

            // Don't paste a result the user aborted while it was being finalized.
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
    ///     Runs the prompt action, streaming into the palette when present. A user
    ///     abort propagates as <see cref="OperationCanceledException" /> with no
    ///     fallback; a streaming fault, empty stream, or unforced timeout falls back
    ///     to a fresh batch request so a broken streaming path still degrades gracefully.
    /// </summary>
    private async Task<string> RunActionAsync(
        PromptAction action,
        string capturedText,
        PromptPaletteWindow? window,
        CancellationToken userToken
    )
    {
        // Stream into the palette at ~30 Hz; the pump marshals each flush to the UI thread.
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
            // Streaming timed out (no user cancel) — fall back to batch.
            return await BatchAsync(action, capturedText, userToken);
        }

        // Fall back to batch when the pump faulted or yielded nothing. A single
        // empty chunk (bulk-yield path) still sets ReceivedAnyChunk so it is not re-run.
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
        // Fresh timeout so a streaming attempt that burned its budget doesn't starve the batch.
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