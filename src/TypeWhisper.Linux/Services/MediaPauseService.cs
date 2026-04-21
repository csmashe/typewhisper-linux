using System.Diagnostics;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

public sealed class MediaPauseService : IMediaPauseService
{
    private readonly HashSet<string> _pausedPlayers = new(StringComparer.OrdinalIgnoreCase);

    public void PauseMedia()
    {
        if (_pausedPlayers.Count > 0)
            return;

        try
        {
            var players = RunCommand("playerctl", "-a --format '{{playerName}} {{status}}' status");
            if (string.IsNullOrWhiteSpace(players))
                return;

            foreach (var line in players.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !string.Equals(parts[1], "Playing", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (RunCommand("playerctl", $"-p {parts[0]} pause") is not null)
                    _pausedPlayers.Add(parts[0]);
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
            return;

        try
        {
            foreach (var player in _pausedPlayers)
                RunCommand("playerctl", $"-p {player} play");
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

    private static string? RunCommand(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                return null;

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
