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

    // Paused MPRIS instance -> consecutive failed resume attempts. Guarded by _playersGate;
    // playerctl itself is always invoked outside the lock.
    private readonly Dictionary<string, int> _pausedPlayers =
        new(StringComparer.Ordinal);

    private readonly Lock _playersGate = new();

    // True between a completed pause scan and the next resume. Kept separate from
    // _pausedPlayers so a player we still owe a resume can't suppress future pause scans.
    private bool _pauseActive;

    // A resume that arrives mid-pause is deferred to the pause's finally, mirroring
    // AudioDuckingService: run now, it would miss the player the pause is still recording.
    private bool _pauseInProgress;
    private bool _resumePending;
    // Set by ResumeMediaForShutdown: refuses later pauses and stops an in-flight one before
    // its next playerctl call, so shutdown only waits for the command already running.
    private bool _shutDown;
    // Set once shutdown starts resuming. A pause command that outlived shutdown's wait lands
    // after that, so the pause resumes again itself.
    private bool _shutdownResumeStarted;
    // Completes once the in-flight pause, including any resume it deferred, has finished.
    private TaskCompletionSource? _pauseSettled;

    public MediaPauseService(IProcessRunner processRunner, IErrorLogService errorLog)
    {
        _processRunner = processRunner;
        _errorLog = errorLog;
    }

    // Test seam: runs just before ResumeMediaForShutdown waits for an in-flight pause.
    internal Action? ShutdownWaitStarting { get; init; }

    public void PauseMedia()
    {
        TaskCompletionSource pauseSettled;
        lock (_playersGate)
        {
            if (_shutDown || _pauseActive)
            {
                return;
            }

            _pauseActive = true;
            _pauseInProgress = true;
            pauseSettled = _pauseSettled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        try
        {
            // Instances, not names: two mpv processes share playerName "mpv".
            var playersResult = RunPlayerctl(
                ["-a", "--format", "{{playerInstance}} {{status}}", "status"]
            );
            if (
                !playersResult.Succeeded
                || string.IsNullOrWhiteSpace(playersResult.StandardOutput)
            )
            {
                return;
            }

            var statuses = playersResult
                .StandardOutput.Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Select(line =>
                    line.Split(
                        ' ',
                        2,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                )
                .Where(parts => parts.Length == 2)
                .ToList();
            var instances = statuses.Select(parts => parts[0]).ToList();

            foreach (
                var parts in statuses.Where(status =>
                    string.Equals(status[1], "Playing", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                if (IsShutDown())
                {
                    return;
                }

                if (!RunPlayerctl(TargetInstance(parts[0], instances, "pause")).Succeeded)
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
            // Players paused before the failure stay tracked: the next resume still owes them.
            Debug.WriteLine($"[MediaPauseService] Pause failed: {ex.Message}");
            lock (_playersGate)
            {
                _pauseActive = false;
            }
        }
        finally
        {
            bool resumeDeferred;
            lock (_playersGate)
            {
                _pauseInProgress = false;
                resumeDeferred = _shutDown ? _shutdownResumeStarted : _resumePending;
                _resumePending = false;
            }

            try
            {
                if (resumeDeferred)
                {
                    ResumePausedPlayers();
                }
            }
            finally
            {
                pauseSettled.TrySetResult();
            }
        }
    }

    public void ResumeMediaForShutdown(TimeSpan settleTimeout)
    {
        Task? inFlightPause;
        lock (_playersGate)
        {
            _shutDown = true;
            inFlightPause = _pauseInProgress ? _pauseSettled?.Task : null;
        }

        // playerctl's own timeout bounds the one command still running.
        if (inFlightPause is not null)
        {
            ShutdownWaitStarting?.Invoke();
            if (!inFlightPause.Wait(settleTimeout))
            {
                WriteDiagnostic(
                    $"[MediaPauseService] Pause still running after {settleTimeout.TotalMilliseconds:0} ms at shutdown; resuming without it."
                );
            }
        }

        lock (_playersGate)
        {
            _shutdownResumeStarted = true;
        }

        // Never deferred: shutdown exits right after this returns.
        ResumePausedPlayers();
    }

    public void ResumeMedia()
    {
        lock (_playersGate)
        {
            if (_pauseInProgress)
            {
                _resumePending = true;
                return;
            }
        }

        ResumePausedPlayers();
    }

    private bool IsShutDown()
    {
        lock (_playersGate)
        {
            return _shutDown;
        }
    }

    private void ResumePausedPlayers()
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

        var liveInstances = ListInstances();
        foreach (var player in players)
        {
            string failure;
            try
            {
                // "-p mpv" would fall through to a surviving "mpv.instance-…" sibling.
                if (
                    liveInstances is not null
                    && !liveInstances.Contains(player, StringComparer.Ordinal)
                    && liveInstances.Any(instance => IsSiblingInstance(player, instance))
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

                var result = RunPlayerctl(TargetInstance(player, liveInstances ?? [], "play"));
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

    /// <summary>Returns the current MPRIS instances, or null when they could not be listed.</summary>
    private List<string>? ListInstances()
    {
        try
        {
            var result = RunPlayerctl(["-l"]);
            return result.Succeeded
                ? [
                    .. result.StandardOutput.Split(
                        '\n',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    ),
                ]
                : null;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"[MediaPauseService] Listing players failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Builds arguments that address exactly <paramref name="instance" />. playerctl's
    ///     <c>-p</c> also matches <c>instance.*</c> siblings, so those are ignored explicitly.
    /// </summary>
    private static List<string> TargetInstance(
        string instance,
        IEnumerable<string> knownInstances,
        string command
    )
    {
        List<string> arguments = ["-p", instance];
        var siblings = knownInstances
            .Where(known => IsSiblingInstance(instance, known))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (siblings.Count > 0)
        {
            arguments.Add("-i");
            arguments.Add(string.Join(',', siblings));
        }

        arguments.Add(command);
        return arguments;
    }

    private static bool IsSiblingInstance(string instance, string candidate)
    {
        return candidate.Length > instance.Length + 1
               && candidate.StartsWith(instance + ".", StringComparison.Ordinal);
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
