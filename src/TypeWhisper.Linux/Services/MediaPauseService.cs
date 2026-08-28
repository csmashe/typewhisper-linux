using System.Diagnostics;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Pauses MPRIS2-compatible media players during dictation via
///     <c>playerctl</c> and resumes them afterward. Silently no-ops when
///     playerctl is absent or no players are currently playing.
/// </summary>
public sealed class MediaPauseService : IMediaPauseService, IDisposable
{
    private static readonly TimeSpan s_playerctlTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly IReadOnlyDictionary<string, string> s_playerctlEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    // A player that never resumes would otherwise pin _pausedPlayers non-empty and disable
    // pausing for the rest of the session, so each one is dropped after this many failures.
    private const int MaxResumeAttempts = 3;

    private readonly IProcessRunner _processRunner;
    private readonly IErrorLogService _errorLog;

    // Paused player name -> consecutive failed resume attempts. Guarded by _playersGate;
    // playerctl itself is always invoked outside the lock.
    private readonly Dictionary<string, int> _pausedPlayers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _playersGate = new();

    // True between a completed pause scan and the next resume. Kept separate from
    // _pausedPlayers so a player we still owe a resume can't suppress future pause scans.
    private bool _pauseActive;

    public MediaPauseService(IProcessRunner processRunner, IErrorLogService errorLog)
    {
        _processRunner = processRunner;
        _errorLog = errorLog;
    }

    public void PauseMedia()
    {
        lock (_playersGate)
        {
            if (_pauseActive)
            {
                return;
            }

            _pauseActive = true;
        }

        try
        {
            var playersResult = RunPlayerctl(
                ["-a", "--format", "{{playerName}} {{status}}", "status"]
            );
            if (
                !playersResult.Succeeded
                || string.IsNullOrWhiteSpace(playersResult.StandardOutput)
            )
            {
                return;
            }

            foreach (
                var line in playersResult.StandardOutput.Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            )
            {
                var parts = line.Split(
                    ' ',
                    2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                );
                if (
                    parts.Length != 2
                    || !string.Equals(parts[1], "Playing", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                if (!RunPlayerctl(["-p", parts[0], "pause"]).Succeeded)
                {
                    continue;
                }

                lock (_playersGate)
                {
                    _pausedPlayers[parts[0]] = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaPauseService] Pause failed: {ex.Message}");
            lock (_playersGate)
            {
                _pausedPlayers.Clear();
                _pauseActive = false;
            }
        }
    }

    public void ResumeMedia()
    {
        string[] players;
        lock (_playersGate)
        {
            _pauseActive = false;
            if (_pausedPlayers.Count == 0)
            {
                return;
            }

            players = [.. _pausedPlayers.Keys];
        }

        foreach (var player in players)
        {
            string failure;
            try
            {
                var result = RunPlayerctl(["-p", player, "play"]);
                if (result.Succeeded)
                {
                    lock (_playersGate)
                    {
                        _pausedPlayers.Remove(player);
                    }

                    continue;
                }

                // playerctl reports a vanished player on stderr with a trailing newline;
                // trim before the exact match or the classifier never fires in production.
                if (
                    string.Equals(
                        result.StandardError.Trim(),
                        "No players found",
                        StringComparison.Ordinal
                    )
                )
                {
                    WriteDiagnostic(
                        $"[MediaPauseService] Player {player} vanished; treating resume as completed."
                    );
                    lock (_playersGate)
                    {
                        _pausedPlayers.Remove(player);
                    }

                    continue;
                }

                failure = DescribeFailure(result);
            }
            catch (Exception ex)
            {
                failure = $"exception: {ex.Message}";
            }

            RecordResumeFailure(player, failure);
        }
    }

    /// <summary>
    ///     Reports the failure and stops retrying the player after <see cref="MaxResumeAttempts" />
    ///     attempts — typically one that exited while paused, which would otherwise cost a
    ///     playerctl round trip on every later recording.
    /// </summary>
    private void RecordResumeFailure(string player, string failure)
    {
        bool retired;
        lock (_playersGate)
        {
            if (!_pausedPlayers.TryGetValue(player, out var attempts))
            {
                return;
            }

            attempts++;
            retired = attempts >= MaxResumeAttempts;
            if (retired)
            {
                // A generation token would be needed to eliminate recycled player-name
                // restores; bounded eviction only limits that stale-identity exposure.
                _pausedPlayers.Remove(player);
            }
            else
            {
                _pausedPlayers[player] = attempts;
            }
        }

        ReportResumeFailure(
            retired
                ? $"Failed to resume media player {player}: {failure}. Giving up after {MaxResumeAttempts} attempts."
                : $"Failed to resume media player {player}: {failure}"
        );
    }

    public void Dispose()
    {
        ResumeMedia();
    }

    private ProcessRunResult RunPlayerctl(IReadOnlyList<string> arguments)
    {
        return _processRunner
            .RunAsync(
                "playerctl",
                arguments,
                environment: s_playerctlEnvironment,
                timeout: s_playerctlTimeout
            )
            .GetAwaiter()
            .GetResult();
    }

    private void ReportResumeFailure(string message)
    {
        WriteDiagnostic($"[MediaPauseService] {message}");
        try
        {
            _errorLog.AddEntry(message);
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"[MediaPauseService] Error reporting failed: {ex.Message}");
        }
    }

    private static string DescribeFailure(ProcessRunResult result)
    {
        var outcome = !result.Started
            ? "process did not start (Started=false)"
            : result.TimedOut
                ? "process timed out (TimedOut=true)"
                : $"process exited with ExitCode={result.ExitCode}";
        var error = result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(error) ? outcome : $"{outcome}; error: {error}";
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            Debug.WriteLine(message);
        }
        catch
        {
            // Restoration and retries must not depend on diagnostic output.
        }
    }
}
