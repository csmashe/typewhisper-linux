using System.Diagnostics;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Pauses MPRIS2-compatible media players during dictation via
///     <c>playerctl</c> and resumes them afterward. Silently no-ops when
///     playerctl is absent or no players are currently playing.
/// </summary>
public sealed class MediaPauseService : IMediaPauseService
{
    private readonly HashSet<string> _pausedPlayers = new(StringComparer.OrdinalIgnoreCase);

    public void PauseMedia()
    {
        if (_pausedPlayers.Count > 0)
        {
            return;
        }

        try
        {
            var players = RunCommand(
                "playerctl",
                "-a",
                "--format",
                "{{playerName}} {{status}}",
                "status"
            );
            if (string.IsNullOrWhiteSpace(players))
            {
                return;
            }

            foreach (
                var line in players.Split(
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

                if (RunCommand("playerctl", "-p", parts[0], "pause") is not null)
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

        try
        {
            foreach (var player in _pausedPlayers)
            {
                RunCommand("playerctl", "-p", player, "play");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaPauseService] Resume failed: {ex.Message}");
        }
        finally
        {
            _pausedPlayers.Clear();
        }
    }

    private static string? RunCommand(string fileName, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            // Force a stable, parseable locale for command output.
            psi.Environment["LC_ALL"] = "C";

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
