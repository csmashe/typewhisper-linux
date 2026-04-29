using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

public sealed class AudioDuckingService : IAudioDuckingService
{
    private static readonly Regex VolumePercentRegex = new(@"(\d+)%", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _savedVolumes = new(StringComparer.Ordinal);
    private bool _isDucked;

    public void DuckAudio(float factor)
    {
        if (_isDucked)
            return;

        try
        {
            var sinkInputs = RunCommand("pactl", "list short sink-inputs");
            if (string.IsNullOrWhiteSpace(sinkInputs))
                return;

            foreach (var line in sinkInputs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var inputId = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(inputId))
                    continue;

                var currentVolume = GetSinkInputVolume(inputId);
                if (string.IsNullOrWhiteSpace(currentVolume))
                    continue;

                _savedVolumes[inputId] = currentVolume;
                var duckedVolume = ScaleVolume(currentVolume, factor);
                RunCommand("pactl", $"set-sink-input-volume {inputId} {duckedVolume}");
            }

            _isDucked = _savedVolumes.Count > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDuckingService] Duck failed: {ex.Message}");
            _savedVolumes.Clear();
            _isDucked = false;
        }
    }

    public void RestoreAudio()
    {
        if (!_isDucked)
            return;

        try
        {
            foreach (var (inputId, volume) in _savedVolumes)
                RunCommand("pactl", $"set-sink-input-volume {inputId} {volume}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDuckingService] Restore failed: {ex.Message}");
        }
        finally
        {
            _savedVolumes.Clear();
            _isDucked = false;
        }
    }

    private static string? GetSinkInputVolume(string inputId)
    {
        var output = RunCommand("pactl", $"get-sink-input-volume {inputId}");
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = VolumePercentRegex.Match(output);
        return match.Success ? match.Groups[1].Value + "%" : null;
    }

    private static string ScaleVolume(string volumePercent, float factor)
    {
        var numericPart = volumePercent.Trim().TrimEnd('%');
        if (!float.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return volumePercent;

        var scaled = Math.Clamp(percent * factor, 0f, 150f);
        return $"{scaled.ToString("0.##", CultureInfo.InvariantCulture)}%";
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
