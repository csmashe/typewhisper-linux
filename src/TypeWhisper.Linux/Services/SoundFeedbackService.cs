using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Plays bundled dictation cue WAVs (start/stop/success/error) via
///     <c>pw-play</c>, <c>paplay</c>, or <c>aplay</c>. Shells out instead of
///     using libcanberra so cues play regardless of the desktop sound theme and
///     GNOME's "System Sounds" toggle (libcanberra respected that toggle).
/// </summary>
public sealed class SoundFeedbackService
{
    internal static readonly TimeSpan s_startCueTimeout = TimeSpan.FromSeconds(2);

    private static readonly string s_soundsDir =
        Path.Join(AppContext.BaseDirectory, "Resources", "Sounds");

    private readonly IProcessRunner _processRunner;
    private readonly string _soundsDir;

    // ReSharper disable once UnusedMember.Global -- resolved by DI (AddSingleton<SoundFeedbackService>).
    public SoundFeedbackService(IProcessRunner processRunner)
        : this(processRunner, PcmPlayerResolver.Resolve()?.AbsolutePath, s_soundsDir)
    {
    }

    internal SoundFeedbackService(
        IProcessRunner processRunner,
        string? player,
        string soundsDir
    )
    {
        _processRunner = processRunner;
        PlayerPath = player;
        _soundsDir = soundsDir;
    }

    internal string? PlayerPath { get; }

    /// <summary>
    ///     Plays the startup cue to completion before capture opens. The process
    ///     runner kills and reaps a player that exceeds the finite cue budget.
    ///     Missing players/files and playback failures remain optional no-ops.
    /// </summary>
    internal Task PlayRecordingStartedAsync(CancellationToken ct = default)
    {
        return PlayAsync("start.wav", s_startCueTimeout, ct);
    }

    public void PlayRecordingStopped()
    {
        Observe(PlayAsync("stop.wav", s_startCueTimeout));
    }

    public void PlaySuccess()
    {
        Observe(PlayAsync("success.wav", s_startCueTimeout));
    }

    public void PlayError()
    {
        Observe(PlayAsync("error.wav", s_startCueTimeout));
    }

    private async Task PlayAsync(string fileName, TimeSpan timeout, CancellationToken ct = default)
    {
        if (PlayerPath is null)
        {
            return;
        }

        var path = Path.Join(_soundsDir, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            _ = await _processRunner
                .RunAsync(PlayerPath, [path], timeout: timeout, ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Optional platform feedback only. IProcessRunner has already killed
            // and reaped the process tree before cancellation is surfaced.
            Trace.WriteLine($"[SoundFeedback] {fileName} playback failed: {ex.Message}");
        }
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            completed => Trace.WriteLine($"[SoundFeedback] Playback task failed: {completed.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

}
