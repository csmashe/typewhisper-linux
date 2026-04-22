using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

public sealed class SoundFeedbackService
{
    public void PlayRecordingStarted() => PlayCanberraEvent("microphone-sensitivity-high");

    public void PlayRecordingStopped() => PlayCanberraEvent("microphone-sensitivity-low");

    private static void PlayCanberraEvent(string eventId)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("canberra-gtk-play", $"-i {eventId}")
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
            });
        }
        catch
        {
            // Optional platform feedback only.
        }
    }
}
