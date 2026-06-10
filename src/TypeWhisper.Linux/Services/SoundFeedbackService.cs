using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Plays the bundled dictation cue WAVs (start / stop / success / error)
///     by shelling out to whatever PCM player is on PATH — <c>pw-play</c>,
///     <c>paplay</c>, or <c>aplay</c>. This mirrors the Windows build (which
///     ships its own sounds and plays them via NAudio) instead of relying on
///     libcanberra/XDG theme events, so the cues play regardless of the desktop
///     sound theme AND regardless of GNOME's "System Sounds" master toggle
///     (libcanberra honored that toggle and refused to play anything when it
///     was off). Fire-and-forget; silently no-ops when no player or sound file
///     is available so recording start/stop is never delayed by audio.
/// </summary>
public sealed class SoundFeedbackService
{
    private static readonly string SoundsDir =
        Path.Join(AppContext.BaseDirectory, "Resources", "Sounds");

    // First available PCM player on PATH, resolved once. pw-play and paplay
    // route through PipeWire / PulseAudio; aplay is the ALSA fallback. All
    // three play a plain 16-bit PCM WAV given as a positional argument.
    private static readonly string? Player = ResolvePlayer();

    public void PlayRecordingStarted() => Play("start.wav");

    public void PlayRecordingStopped() => Play("stop.wav");

    public void PlaySuccess() => Play("success.wav");

    public void PlayError() => Play("error.wav");

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
        // Probe PATH through the same helper SystemCommandAvailabilityService
        // uses for HasAudioPlayer (same candidate order), so Player is non-null
        // exactly when HasAudioPlayer is true. A divergent probe here (e.g. not
        // trimming PATH entries) could leave Player null while HasAudioPlayer
        // reports true, silently no-oping every cue.
        return Array.Find(
            new[] { "pw-play", "paplay", "aplay" },
            SystemCommandAvailabilityService.IsCommandAvailable
        );
    }
}
