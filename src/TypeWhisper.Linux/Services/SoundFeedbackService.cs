using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Plays XDG sound theme events to give audible start/stop feedback.
/// Uses <c>canberra-gtk-play</c> with the standard microphone sensitivity
/// event IDs. Silently no-ops when the tool is absent; the async wait is
/// fire-and-forget so recording start/stop is never delayed by audio.
/// </summary>
public sealed class SoundFeedbackService
{
    public void PlayRecordingStarted() => PlayCanberraEvent("microphone-sensitivity-high");

    public void PlayRecordingStopped() => PlayCanberraEvent("microphone-sensitivity-low");

    private static void PlayCanberraEvent(string eventId)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo("canberra-gtk-play", $"-i {eventId}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    process.WaitForExit(1500);
                }
                catch
                {
                    // Best-effort only.
                }
                finally
                {
                    process.Dispose();
                }
            });
        }
        catch
        {
            // Optional platform feedback only.
        }
    }
}
