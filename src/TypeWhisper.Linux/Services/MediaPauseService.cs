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

    private readonly IProcessRunner _processRunner;
    private readonly IErrorLogService _errorLog;
    private readonly HashSet<string> _pausedPlayers = new(StringComparer.OrdinalIgnoreCase);

    public MediaPauseService(IProcessRunner processRunner, IErrorLogService errorLog)
    {
        _processRunner = processRunner;
        _errorLog = errorLog;
    }

    public void PauseMedia()
    {
        if (_pausedPlayers.Count > 0)
        {
            return;
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

                if (RunPlayerctl(["-p", parts[0], "pause"]).Succeeded)
                {
                    _pausedPlayers.Add(parts[0]);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaPauseService] Pause failed: {ex.Message}");
            _pausedPlayers.Clear();
        }
    }

    public void ResumeMedia()
    {
        if (_pausedPlayers.Count == 0)
        {
            return;
        }

        foreach (var player in _pausedPlayers.ToArray())
        {
            try
            {
                var result = RunPlayerctl(["-p", player, "play"]);
                if (result.Succeeded)
                {
                    _pausedPlayers.Remove(player);
                    continue;
                }

                ReportResumeFailure(
                    $"Failed to resume media player {player}: {DescribeFailure(result)}"
                );
            }
            catch (Exception ex)
            {
                ReportResumeFailure(
                    $"Failed to resume media player {player}: exception: {ex.Message}"
                );
            }
        }
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
