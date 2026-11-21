using System.Diagnostics;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Announces a new release with a desktop notification, so tray-only users who never open
///     the main window still hear about it.
/// </summary>
public sealed class UpdateNotificationService(IProcessRunner runner)
{
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(3);

    public async Task NotifyAsync(string version)
    {
        try
        {
            await runner
                .RunAsync(
                    "gdbus",
                    [
                        "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path",
                        "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify",
                        "--",
                        "TypeWhisper", "0", RecordingNotificationService.ResolveIconPath(),
                        Loc.Instance.GetString("Update.NotificationSummary", version),
                        Loc.Instance["Update.NotificationBody"],
                        "[]", // actions
                        "{}", // hints
                        "-1", // expire_timeout: daemon default
                    ],
                    timeout: s_callTimeout
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UpdateNotification] Could not show the update notification: {ex.Message}");
        }
    }
}
