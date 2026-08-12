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
public sealed partial class AudioDuckingService : IAudioDuckingService, IDisposable
{
    private const double MaximumRawVolume = 98_304d;
    private const int MaxRestoreAttempts = 3;
    private static readonly TimeSpan s_pactlTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly IReadOnlyDictionary<string, string> s_pactlEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    // "Sink Input #593" — block header in `pactl list sink-inputs` output.
    [GeneratedRegex(@"^Sink Input #(\d+)")]
    private static partial Regex SinkInputIdRegex();

    // Raw pa_volume_t followed by its percentage representation.
    [GeneratedRegex(@"(?<![-0-9.])([0-9]+)\s*/\s*[0-9]+%")]
    private static partial Regex RawVolumeRegex();

    private readonly IProcessRunner _processRunner;
    private readonly IErrorLogService _errorLog;
    private readonly Dictionary<string, SavedVolumeState> _savedVolumes =
        new(StringComparer.Ordinal);
    private bool _isDucked;

    public AudioDuckingService(IProcessRunner processRunner, IErrorLogService errorLog)
    {
        _processRunner = processRunner;
        _errorLog = errorLog;
    }

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
            var listingResult = RunPactl(["list", "sink-inputs"]);
            if (
                !listingResult.Succeeded
                || string.IsNullOrWhiteSpace(listingResult.StandardOutput)
            )
            {
                return;
            }

            foreach (
                var (inputId, currentVolumes) in ParseSinkInputVolumes(
                    listingResult.StandardOutput
                )
            )
            {
                var savedVolumes = currentVolumes.ToArray();
                _savedVolumes[inputId] = new SavedVolumeState(savedVolumes, 0);
                var duckedVolumes = savedVolumes
                    .Select(volume => ScaleVolume(volume, factor))
                    .ToArray();
                _ = SetSinkInputVolume(inputId, duckedVolumes);
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

        foreach (var (inputId, state) in _savedVolumes.ToArray())
        {
            try
            {
                var result = SetSinkInputVolume(inputId, state.Volumes);
                if (result.Succeeded)
                {
                    _savedVolumes.Remove(inputId);
                    continue;
                }

                // pactl reports a vanished sink-input on stderr with a trailing newline;
                // trim before the exact match or the classifier never fires in production.
                if (
                    string.Equals(
                        result.StandardError.Trim(),
                        "Failure: No such entity",
                        StringComparison.Ordinal
                    )
                )
                {
                    WriteDiagnostic(
                        $"[AudioDuckingService] Sink input {inputId} vanished; treating restore as completed."
                    );
                    _savedVolumes.Remove(inputId);
                    continue;
                }

                RecordRestoreFailure(inputId, state, DescribeFailure(result));
            }
            catch (Exception ex)
            {
                RecordRestoreFailure(inputId, state, $"exception: {ex.Message}");
            }
        }

        _isDucked = _savedVolumes.Count > 0;
    }

    public void Dispose()
    {
        RestoreAudio();
    }

    private void RecordRestoreFailure(string inputId, SavedVolumeState state, string failure)
    {
        // Never write back an entry that is no longer tracked — a concurrent pass may
        // have completed it, and resurrecting it would retry a restore that already
        // happened (or worse, re-apply stale volumes to a recycled sink-input id).
        if (!_savedVolumes.ContainsKey(inputId))
        {
            return;
        }

        var attempts = state.FailedRestoreAttempts + 1;
        var retired = attempts >= MaxRestoreAttempts;
        if (retired)
        {
            // A generation token would be needed to eliminate recycled pactl-ID restores;
            // bounded eviction only limits that stale-identity exposure.
            _savedVolumes.Remove(inputId);
        }
        else
        {
            _savedVolumes[inputId] = state with { FailedRestoreAttempts = attempts };
        }

        ReportRestoreFailure(
            retired
                ? $"Failed to restore sink input {inputId}: {failure}. Giving up after {MaxRestoreAttempts} attempts."
                : $"Failed to restore sink input {inputId}: {failure}"
        );
    }

    /// <summary>
    ///     Walks the <c>pactl list sink-inputs</c> output, yielding every raw
    ///     channel volume from the first "Volume:" line of each "Sink Input #N" block.
    /// </summary>
    private static IEnumerable<(string Id, string[] Volumes)> ParseSinkInputVolumes(
        string listing
    )
    {
        string? currentId = null;

        foreach (var line in listing.Split('\n').Select(raw => raw.Trim()))
        {
            var idMatch = SinkInputIdRegex().Match(line);
            if (idMatch.Success)
            {
                currentId = idMatch.Groups[1].Value;
                continue;
            }

            if (currentId is null || !line.StartsWith("Volume:", StringComparison.Ordinal))
            {
                continue;
            }

            var volumes = RawVolumeRegex()
                .Matches(line)
                .Select(match => match.Groups[1].Value)
                .ToArray();
            if (volumes.Length > 0)
            {
                yield return (currentId, volumes);
            }

            // Only the first Volume line per block is relevant.
            currentId = null;
        }
    }

    private ProcessRunResult SetSinkInputVolume(string inputId, string[] volumes)
    {
        var arguments = new List<string>(2 + volumes.Length)
        {
            "set-sink-input-volume",
            inputId,
        };
        arguments.AddRange(volumes);
        return RunPactl(arguments);
    }

    private ProcessRunResult RunPactl(IReadOnlyList<string> arguments)
    {
        return _processRunner
            .RunAsync(
                "pactl",
                arguments,
                environment: s_pactlEnvironment,
                timeout: s_pactlTimeout
            )
            .GetAwaiter()
            .GetResult();
    }

    private void ReportRestoreFailure(string message)
    {
        WriteDiagnostic($"[AudioDuckingService] {message}");
        try
        {
            _errorLog.AddEntry(message);
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"[AudioDuckingService] Error reporting failed: {ex.Message}");
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

    private static string ScaleVolume(string rawVolume, float factor)
    {
        if (
            !ulong.TryParse(
                rawVolume,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var numericVolume
            )
        )
        {
            return rawVolume;
        }

        var scaled = Math.Clamp(numericVolume * (double)factor, 0d, MaximumRawVolume);
        var rounded = Math.Round(scaled, MidpointRounding.AwayFromZero);
        return rounded.ToString("0", CultureInfo.InvariantCulture);
    }

    private sealed record SavedVolumeState(string[] Volumes, int FailedRestoreAttempts);
}
