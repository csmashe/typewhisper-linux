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
    // pactl 16+ looks the sink input up before setting its volume, so a vanished
    // stream fails that lookup instead of the older "Failure:" wording.
    private static readonly HashSet<string> s_vanishedSinkInputErrors = new(StringComparer.Ordinal)
    {
        "Failure: No such entity",
        "Failed to get sink input information: No such entity",
    };

    // "Sink Input #593" — block header in `pactl list sink-inputs` output.
    [GeneratedRegex(@"^Sink Input #(\d+)")]
    private static partial Regex SinkInputIdRegex();

    // Raw pa_volume_t followed by its percentage representation.
    [GeneratedRegex(@"(?<![-0-9.])([0-9]+)\s*/\s*[0-9]+%")]
    private static partial Regex RawVolumeRegex();

    // "Cookie: 3922:700c" in `pactl info`. PulseAudio and pipewire-pulse pick a new
    // cookie on every server start, which makes it the server-instance token.
    [GeneratedRegex(@"^Cookie:\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ServerCookieRegex();

    private readonly IProcessRunner _processRunner;
    private readonly IErrorLogService _errorLog;
    // Guarded by _volumesGate, mirroring MediaPauseService's _playersGate:
    // RestoreAudio is reachable from async continuations and Dispose, so two
    // threads can otherwise enumerate/mutate the dictionary concurrently.
    // pactl round trips stay OUTSIDE the gate; only a duck's set-volume is LAUNCHED inside it.
    private readonly Dictionary<string, SavedVolumeState> _savedVolumes =
        new(StringComparer.Ordinal);
    private readonly Lock _volumesGate = new();
    // _isDucked stays false until a duck COMPLETES: RestoreAudio must not run while a
    // duck is mid-flight, or a restore could put an original volume back and remove
    // the entry just before the duck's queued set-volume lands — leaving the stream
    // ducked with nothing tracking it. _duckInProgress separately blocks duck
    // re-entry so the two guards can't be satisfied by the same half-open state.
    private bool _duckInProgress;
    // A restore that arrived mid-duck is deferred, not dropped: the duck's finally runs
    // it once the last set-volume has landed. Dropping it would leave every stream the
    // duck just lowered quiet forever when shutdown or session loss races the duck.
    private bool _restorePending;
    private bool _isDucked;
    // Set by RestoreAudioForShutdown under the gate; no duck set-volume starts after it.
    private bool _shutDown;
    // The running duck set-volume, registered in the critical section that launched it.
    private InFlightDuck? _inFlightDuck;

    public AudioDuckingService(IProcessRunner processRunner, IErrorLogService errorLog)
    {
        _processRunner = processRunner;
        _errorLog = errorLog;
    }

    // Test seam: runs just before RestoreAudioForShutdown waits for an in-flight set-volume.
    internal Action? ShutdownWaitStarting { get; init; }

    // Test seam: runs under the gate, after the shutdown check and before the set-volume launches.
    internal Action? DuckCommandLaunching { get; init; }

    // Test seam: runs after a set-volume exits, before the duck settles it.
    internal Action? DuckCommandExited { get; init; }

    public void DuckAudio(float factor)
    {
        lock (_volumesGate)
        {
            if (_shutDown || _isDucked || _duckInProgress)
            {
                return;
            }

            _duckInProgress = true;
        }

        try
        {
            // The cookie must read the same on both sides of the listing: a restart in
            // between would pair one server's volumes with another server's ids.
            var serverCookie = ReadServerCookie();
            if (serverCookie is null || IsShutDown())
            {
                return;
            }

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

            var sinkInputs = ParseSinkInputVolumes(listingResult.StandardOutput).ToArray();
            if (sinkInputs.Length == 0 || IsShutDown())
            {
                return;
            }

            if (!string.Equals(ReadServerCookie(), serverCookie, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var (inputId, currentVolumes) in sinkInputs)
            {
                var duckedVolumes = currentVolumes
                    .Select(volume => ScaleVolume(volume, factor))
                    .ToArray();
                InFlightDuck duck;
                lock (_volumesGate)
                {
                    // The check, the save and the launch share one critical section, so a
                    // shutdown either refuses this set or finds it registered and waits for it.
                    if (_shutDown)
                    {
                        return;
                    }

                    // Never overwrite: an entry that survived a failed restore still
                    // holds the ORIGINAL volume — replacing it here would capture the
                    // already-ducked value and lower that stream permanently.
                    if (!_savedVolumes.TryGetValue(inputId, out var savedState))
                    {
                        savedState = new SavedVolumeState([.. currentVolumes], serverCookie, 0);
                        _savedVolumes.Add(inputId, savedState);
                    }

                    DuckCommandLaunching?.Invoke();
                    // RunAsync returns once pactl has started, so only the launch holds the gate.
                    duck = _inFlightDuck = new InFlightDuck(
                        inputId,
                        savedState.Volumes,
                        savedState.ServerCookie,
                        StartSetSinkInputVolume(inputId, duckedVolumes)
                    );
                }

                try
                {
                    _ = duck.Command.GetAwaiter().GetResult();
                }
                finally
                {
                    DuckCommandExited?.Invoke();
                    bool landedAfterShutdownRestore;
                    lock (_volumesGate)
                    {
                        landedAfterShutdownRestore = SettleDuck(duck);
                    }

                    if (landedAfterShutdownRestore)
                    {
                        UndoLateDuck(duck);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Entries saved before the failure are kept: they hold the only copy of the
            // original volumes for streams already ducked, and RestoreAudio needs them.
            Debug.WriteLine($"[AudioDuckingService] Duck failed: {ex.Message}");
        }
        finally
        {
            bool restoreDeferred;
            lock (_volumesGate)
            {
                _duckInProgress = false;
                _isDucked = _savedVolumes.Count > 0;
                // Shutdown restores everything itself and never defers to the duck.
                restoreDeferred = !_shutDown && _restorePending && _isDucked;
                _restorePending = false;
            }

            // Outside the gate: the restore retakes it and runs pactl round trips.
            if (restoreDeferred)
            {
                RestoreSavedVolumes();
            }
        }
    }

    public void RestoreAudioForShutdown(TimeSpan settleTimeout)
    {
        InFlightDuck? inFlightDuck;
        lock (_volumesGate)
        {
            _shutDown = true;
            inFlightDuck = _inFlightDuck;
        }

        if (inFlightDuck is not null)
        {
            SettleForShutdown(inFlightDuck, settleTimeout);
        }

        // Never deferred: shutdown exits right after this returns.
        RestoreSavedVolumes();
    }

    // The runner kills pactl at its timeout, so the set-volume normally exits within the wait.
    private void SettleForShutdown(InFlightDuck inFlightDuck, TimeSpan settleTimeout)
    {
        ShutdownWaitStarting?.Invoke();
        WaitForExit(inFlightDuck.Command, settleTimeout);

        bool stillRunning;
        var landedAfterShutdownRestore = false;
        lock (_volumesGate)
        {
            stillRunning = inFlightDuck is { Settled: false, Command.IsCompleted: false };
            if (stillRunning)
            {
                // Restored without it; the duck undoes this set itself once it exits.
                inFlightDuck.LandsAfterShutdownRestore = true;
            }
            else
            {
                // An earlier shutdown call may have restored without it; whoever settles it owes the undo.
                landedAfterShutdownRestore = SettleDuck(inFlightDuck);
            }
        }

        if (landedAfterShutdownRestore)
        {
            UndoLateDuck(inFlightDuck);
        }

        if (stillRunning)
        {
            WriteDiagnostic(
                $"[AudioDuckingService] Set-volume still running after {settleTimeout.TotalMilliseconds:0} ms at shutdown; restoring without it."
            );
        }
    }

    public void RestoreAudio()
    {
        lock (_volumesGate)
        {
            if (_duckInProgress)
            {
                _restorePending = true;
                return;
            }

            if (!_isDucked)
            {
                return;
            }
        }

        RestoreSavedVolumes();
    }

    private bool IsShutDown()
    {
        lock (_volumesGate)
        {
            return _shutDown;
        }
    }

    // Caller holds _volumesGate and the set has finished. Returns true when it lowered the
    // stream after shutdown's restore started, so the caller must undo it.
    private bool SettleDuck(InFlightDuck duck)
    {
        if (duck.Settled)
        {
            return false;
        }

        duck.Settled = true;
        if (ReferenceEquals(_inFlightDuck, duck))
        {
            _inFlightDuck = null;
        }

        return duck is
        {
            LandsAfterShutdownRestore: true,
            Command: { IsCompletedSuccessfully: true, Result.Succeeded: true },
        };
    }

    // Shutdown already restored and dropped this stream's entry, so the late set is undone
    // straight from the duck's own copy of the original volume.
    private void UndoLateDuck(InFlightDuck duck)
    {
        try
        {
            // The same cookie check as RestoreSavedVolumes: after a restart the id may be reused.
            var currentCookie = ReadServerCookie();
            if (currentCookie is null)
            {
                ReportRestoreFailure(
                    $"Failed to restore sink input {duck.InputId} after a late duck: could not read the audio server cookie"
                );
                return;
            }

            if (!string.Equals(currentCookie, duck.ServerCookie, StringComparison.Ordinal))
            {
                WriteDiagnostic(
                    $"[AudioDuckingService] Audio server restarted since sink input {duck.InputId} was ducked; not restoring onto a possibly reused id."
                );
                return;
            }

            var result = SetSinkInputVolume(duck.InputId, duck.OriginalVolumes);
            if (!result.Succeeded)
            {
                ReportRestoreFailure(
                    $"Failed to restore sink input {duck.InputId} after a late duck: {DescribeFailure(result)}"
                );
            }
        }
        catch (Exception ex)
        {
            ReportRestoreFailure(
                $"Failed to restore sink input {duck.InputId} after a late duck: exception: {ex.Message}"
            );
        }
    }

    private static void WaitForExit(Task command, TimeSpan timeout)
    {
        try
        {
            command.Wait(timeout);
        }
        catch (AggregateException)
        {
            // A failed command has still finished.
        }
    }

    private void RestoreSavedVolumes()
    {
        KeyValuePair<string, SavedVolumeState>[] entries;
        lock (_volumesGate)
        {
            if (_savedVolumes.Count == 0)
            {
                return;
            }

            entries = [.. _savedVolumes];
        }

        // Ids are only unique within one server run; after a restart an unrelated stream
        // can reuse a saved id, so entries from an earlier server are retired, not applied.
        // pactl has no atomic check-and-set: a restart between this read and the set-volume
        // below is not covered.
        var currentCookie = ReadServerCookie();

        foreach (var (inputId, state) in entries)
        {
            try
            {
                if (currentCookie is null)
                {
                    RecordRestoreFailure(inputId, "could not read the audio server cookie");
                    continue;
                }

                if (!string.Equals(currentCookie, state.ServerCookie, StringComparison.Ordinal))
                {
                    WriteDiagnostic(
                        $"[AudioDuckingService] Audio server restarted since sink input {inputId} was ducked; not restoring onto a possibly reused id."
                    );
                    RemoveSavedVolume(inputId);
                    continue;
                }

                var result = SetSinkInputVolume(inputId, state.Volumes);
                if (result.Succeeded)
                {
                    RemoveSavedVolume(inputId);
                    continue;
                }

                // pactl reports a vanished sink-input on stderr with a trailing newline;
                // trim before the exact match or the classifier never fires in production.
                if (s_vanishedSinkInputErrors.Contains(result.StandardError.Trim()))
                {
                    WriteDiagnostic(
                        $"[AudioDuckingService] Sink input {inputId} vanished; treating restore as completed."
                    );
                    RemoveSavedVolume(inputId);
                    continue;
                }

                RecordRestoreFailure(inputId, DescribeFailure(result));
            }
            catch (Exception ex)
            {
                RecordRestoreFailure(inputId, $"exception: {ex.Message}");
            }
        }

        lock (_volumesGate)
        {
            _isDucked = _savedVolumes.Count > 0;
        }
    }

    private void RemoveSavedVolume(string inputId)
    {
        lock (_volumesGate)
        {
            _savedVolumes.Remove(inputId);
        }
    }

    public void Dispose()
    {
        RestoreAudio();
    }

    private void RecordRestoreFailure(string inputId, string failure)
    {
        bool retired;
        lock (_volumesGate)
        {
            // Never write back an entry that is no longer tracked — a concurrent pass may
            // have completed it, and resurrecting it would retry a restore that already
            // happened (or worse, re-apply stale volumes to a recycled sink-input id).
            // The attempt count comes from the LIVE entry, not the caller's snapshot,
            // so concurrent passes cannot undercount failures.
            if (!_savedVolumes.TryGetValue(inputId, out var current))
            {
                return;
            }

            var attempts = current.FailedRestoreAttempts + 1;
            retired = attempts >= MaxRestoreAttempts;
            if (retired)
            {
                _savedVolumes.Remove(inputId);
            }
            else
            {
                _savedVolumes[inputId] = current with { FailedRestoreAttempts = attempts };
            }
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

    private string? ReadServerCookie()
    {
        try
        {
            var result = RunPactl(["info"]);
            if (!result.Succeeded)
            {
                return null;
            }

            var match = ServerCookieRegex().Match(result.StandardOutput);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"[AudioDuckingService] Reading the server cookie failed: {ex.Message}");
            return null;
        }
    }

    private ProcessRunResult SetSinkInputVolume(string inputId, string[] volumes)
    {
        return StartSetSinkInputVolume(inputId, volumes).GetAwaiter().GetResult();
    }

    private Task<ProcessRunResult> StartSetSinkInputVolume(string inputId, string[] volumes)
    {
        var arguments = new List<string>(2 + volumes.Length)
        {
            "set-sink-input-volume",
            inputId,
        };
        arguments.AddRange(volumes);
        return StartPactl(arguments);
    }

    private ProcessRunResult RunPactl(IReadOnlyList<string> arguments)
    {
        return StartPactl(arguments).GetAwaiter().GetResult();
    }

    private Task<ProcessRunResult> StartPactl(IReadOnlyList<string> arguments)
    {
        return _processRunner.RunAsync(
            "pactl",
            arguments,
            environment: s_pactlEnvironment,
            timeout: s_pactlTimeout
        );
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

    private sealed record SavedVolumeState(
        string[] Volumes,
        string ServerCookie,
        int FailedRestoreAttempts
    );

    // Settled and LandsAfterShutdownRestore are guarded by _volumesGate.
    private sealed class InFlightDuck(
        string inputId,
        string[] originalVolumes,
        string serverCookie,
        Task<ProcessRunResult> command
    )
    {
        public string InputId { get; } = inputId;
        public string[] OriginalVolumes { get; } = originalVolumes;
        public string ServerCookie { get; } = serverCookie;
        public Task<ProcessRunResult> Command { get; } = command;
        public bool Settled { get; set; }
        public bool LandsAfterShutdownRestore { get; set; }
    }
}
