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
        PluginManager pluginManager
    )
    {
        _services = services;
        _promptActions = promptActions;
        _processing = processing;
        _textInsertion = textInsertion;
        _pluginManager = pluginManager;
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

        var capturedText = await _textInsertion.CaptureSelectedTextAsync();

        PromptAction? selectedAction = null;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = _services.GetRequiredService<PromptPaletteWindow>();
            window.SourceText = capturedText;
            window.SetActions(actions);
            selectedAction = await window.ShowAndWaitAsync();
        });

        if (selectedAction is null || string.IsNullOrWhiteSpace(capturedText))
        {
            return;
        }

        await ExecuteActionAsync(selectedAction, capturedText);
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

        var captured = await _textInsertion.CaptureSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(captured))
        {
            return;
        }

        await ExecuteActionAsync(action, captured);
    }

    private async Task ExecuteActionAsync(PromptAction action, string capturedText)
    {
        if (!_processing.IsAnyProviderAvailable)
        {
            await ShowWarningAsync(
                "TypeWhisper",
                "No LLM provider available. Please configure an API key in Plugins."
            );
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await _processing.ProcessAsync(action, capturedText, cts.Token);
            var actionPlugin = ResolveActionPlugin(action);
            if (actionPlugin is not null)
            {
                var context = new ActionContext(null, null, null, null, capturedText);
                await actionPlugin.ExecuteAsync(result, context, cts.Token);
                return;
            }

            await _textInsertion.InsertTextAsync(result);
        }
        catch (OperationCanceledException)
        {
            /* best effort */
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PromptPalette] Prompt processing failed: {ex}");
            await ShowWarningAsync("TypeWhisper", $"Prompt processing failed: {ex.Message}");
        }
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