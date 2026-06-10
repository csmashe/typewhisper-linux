using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Plays bundled dictation cue WAVs (start/stop/success/error) via
///     <c>pw-play</c>, <c>paplay</c>, or <c>aplay</c>. Shells out instead of
///     using libcanberra so cues play regardless of the desktop sound theme and
///     GNOME's "System Sounds" toggle (libcanberra respected that toggle).
///     Fire-and-forget; silently no-ops when no player or file is available.
/// </summary>
public sealed class SoundFeedbackService
{
    private static readonly string SoundsDir =
        Path.Join(AppContext.BaseDirectory, "Resources", "Sounds");

    // First available player on PATH: pw-play (PipeWire), paplay (PulseAudio), aplay (ALSA).
    private static readonly string? Player = ResolvePlayer();

    public void PlayRecordingStarted()
    {
        Play("start.wav");
    }

    public void PlayRecordingStopped()
    {
        Play("stop.wav");
    }

    public void PlaySuccess()
    {
        Play("success.wav");
    }

    public void PlayError()
    {
        Play("error.wav");
    }

    private static void Play(string fileName)
    {
        if (Player is null)
        {
            return;
        }

        var path = Path.Join(SoundsDir, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(Player)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(path);

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    // Cues are short (≤0.4s); 2s is ample headroom.
                    process.WaitForExit(2000);
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

    private static string? ResolvePlayer()
    {
        // Same candidate order as SystemCommandAvailabilityService.HasAudioPlayer
        // so Player is non-null exactly when HasAudioPlayer is true.
        return Array.Find(
            new[] { "pw-play", "paplay", "aplay" },
            SystemCommandAvailabilityService.IsCommandAvailable
        );
    }
}