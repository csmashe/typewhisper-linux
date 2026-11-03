using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Shared action-plugin execution, result normalization, and completion-event path.
/// </summary>
public sealed class ActionPluginExecutionHost(
    PluginEventBus eventBus,
    ISettingsService settings,
    IErrorLogService errorLog
)
{
    public async Task<ActionPluginExecutionResult> ExecuteAsync(
        IActionPlugin plugin,
        string input,
        ActionContext context,
        string? appName,
        CancellationToken cancellationToken
    )
    {
        var result = await plugin.ExecuteAsync(input, context, cancellationToken);
        var normalized = Normalize(result);

        eventBus.Publish(
            new ActionCompletedEvent
            {
                ActionId = plugin.ActionId,
                Success = normalized.Success,
                Message = normalized.Message,
                Url = normalized.Url,
                Icon = normalized.Icon,
                DisplayDurationMilliseconds = normalized.DisplayDurationMilliseconds,
                AppName = appName,
            }
        );

        return normalized;
    }

    internal ActionPluginExecutionResult Normalize(ActionResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? Loc.Instance[result.Success ? "ActionResult.Completed" : "ActionResult.Failed"]
            : result.Message;
        var url = UrlLauncher.NormalizeHttpUrl(result.Url);
        if (!string.IsNullOrWhiteSpace(result.Url) && url is null)
        {
            var logMessage = $"Action result URL was rejected because it is not absolute HTTP(S): {result.Url}";
            Trace.WriteLine($"[ActionPluginExecutionHost] {logMessage}");
            try
            {
                errorLog.AddEntry(logMessage, ErrorCategory.Plugin);
            }
            catch (Exception ex)
            {
                // Diagnostics must not turn a completed external action into a host failure.
                Trace.WriteLine(
                    $"[ActionPluginExecutionHost] Failed to record rejected URL: {ex.Message}"
                );
            }
        }

        var icon = string.IsNullOrWhiteSpace(result.Icon) ? null : result.Icon.Trim();
        var duration = EffectiveDisplayDurationMilliseconds(result.DisplayDuration);
        return new ActionPluginExecutionResult(result.Success, message, url, icon, duration);
    }

    private int EffectiveDisplayDurationMilliseconds(double requestedSeconds)
    {
        var globalDuration = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
            settings.Current.PreviewBubbleAutoHideMilliseconds
        );
        if (globalDuration == 0)
        {
            return 0;
        }

        if (!double.IsFinite(requestedSeconds))
        {
            return globalDuration;
        }

        var clampedSeconds = Math.Clamp(
            requestedSeconds,
            AppSettings.MinPreviewBubbleAutoHideMilliseconds / 1000d,
            AppSettings.MaxPreviewBubbleAutoHideMilliseconds / 1000d
        );
        return (int)Math.Round(clampedSeconds * 1000d, MidpointRounding.AwayFromZero);
    }
}

public sealed record ActionPluginExecutionResult(
    bool Success,
    string Message,
    string? Url,
    string? Icon,
    int DisplayDurationMilliseconds
);
