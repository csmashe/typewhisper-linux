using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Lowers the volume of all active PulseAudio/PipeWire sink inputs during
///     dictation so that playback doesn't bleed into the microphone capture.
///     Uses <c>pactl</c> — available on PipeWire via pipewire-pulse as well as
///     on native PulseAudio. Silently no-ops when pactl is absent.
/// </summary>
public sealed class AudioDuckingService : IAudioDuckingService
{
    // "Sink Input #593" — block header in `pactl list sink-inputs` output.
    private static readonly Regex s_sinkInputIdRegex = new(
        @"^Sink Input #(\d+)",
        RegexOptions.Compiled
    );

    // First percentage on a "Volume:" line (e.g. "... / 65% / -9.30 dB").
    private static readonly Regex s_volumePercentRegex = new(@"(\d+)%", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _savedVolumes = new(StringComparer.Ordinal);
    private bool _isDucked;

    public void DuckAudio(float factor)
    {
        if (_isDucked)
        {
            return;
        }

        try
        {
            // pactl has no "get-sink-input-volume" subcommand, so read current
            // volumes by parsing the long `list sink-inputs` output instead.
            var listing = RunCommand("pactl", "list", "sink-inputs");
            if (string.IsNullOrWhiteSpace(listing))
            {
                return;
            }

            foreach (var (inputId, currentVolume) in ParseSinkInputVolumes(listing))
            {
                _savedVolumes[inputId] = currentVolume;
                var duckedVolume = ScaleVolume(currentVolume, factor);
                RunCommand("pactl", "set-sink-input-volume", inputId, duckedVolume);
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
        {
            return;
        }

        try
        {
            foreach (var (inputId, volume) in _savedVolumes)
            {
                RunCommand("pactl", "set-sink-input-volume", inputId, volume);
            }
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

    /// <summary>
    ///     Walks the <c>pactl list sink-inputs</c> output, yielding the first
    ///     volume percentage of each "Sink Input #N" block.
    /// </summary>
    private static IEnumerable<(string Id, string Volume)> ParseSinkInputVolumes(string listing)
    {
        string? currentId = null;

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();

            var idMatch = s_sinkInputIdRegex.Match(line);
            if (idMatch.Success)
            {
                currentId = idMatch.Groups[1].Value;
                continue;
            }

            if (currentId is not null && line.StartsWith("Volume:", StringComparison.Ordinal))
            {
                var volMatch = s_volumePercentRegex.Match(line);
                if (volMatch.Success)
                {
                    yield return (currentId, volMatch.Groups[1].Value + "%");
                }

                // Only the first Volume line per block is relevant.
                currentId = null;
            }
        }
    }

    private static string ScaleVolume(string volumePercent, float factor)
    {
        var numericPart = volumePercent.Trim().TrimEnd('%');
        if (
            !float.TryParse(
                numericPart,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var percent
            )
        )
        {
            return volumePercent;
        }

        var scaled = Math.Clamp(percent * factor, 0f, 150f);
        return $"{scaled.ToString("0.##", CultureInfo.InvariantCulture)}%";
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
