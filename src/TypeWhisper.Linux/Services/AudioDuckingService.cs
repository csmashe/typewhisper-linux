using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Lowers the volume of all active PulseAudio/PipeWire sink inputs during
///     dictation so that playback doesn't bleed into the microphone capture.
///     Uses <c>pactl</c> — available on PipeWire via pipewire-pulse as well as
///     on native PulseAudio. Silently no-ops when pactl is absent.
///     <para>
///         The sound server remembers each app's last stream volume (WirePlumber's
///         stream-properties, PulseAudio's module-stream-restore), so a stream that ends
///         while ducked, or a crash mid-recording, would bring that app back quiet for
///         good. Every lowered volume is journaled to disk before it is lowered, and a
///         stream that comes back at the ducked level gets its original volume back.
///     </para>
/// </summary>
public sealed partial class AudioDuckingService : IAudioDuckingService, IDisposable
{
    internal const string JournalFileName = "audio-ducking.json";
    private const double MaximumRawVolume = 98_304d;
    private const int MaxRestoreAttempts = 3;
    // The server stores volumes as floats, so a remembered raw volume can come back a unit or two off.
    private const ulong RawVolumeTolerance = 64;
    private static readonly TimeSpan s_pactlTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan s_newStreamDebounce = TimeSpan.FromMilliseconds(100);
    private static readonly IReadOnlyDictionary<string, string> s_pactlEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };
    // pactl 16+ looks the sink input up before setting its volume, so a vanished
    // stream fails that lookup instead of the older "Failure:" wording.
    private static readonly HashSet<string> s_vanishedSinkInputErrors = new(StringComparer.Ordinal)
    {
        "Failure: No such entity",
        "Failed to get sink input information: No such entity",
    };
    private static readonly JsonSerializerOptions s_journalJsonOptions = new() { WriteIndented = true };

    // "Sink Input #593" — block header in `pactl list sink-inputs` output.
    [GeneratedRegex(@"^Sink Input #(\d+)")]
    private static partial Regex SinkInputIdRegex();

    // Raw pa_volume_t followed by its percentage representation.
    [GeneratedRegex(@"(?<![-0-9.])([0-9]+)\s*/\s*[0-9]+%")]
    private static partial Regex RawVolumeRegex();

    // `key = "value"` lines in a sink input's Properties section.
    [GeneratedRegex("""^([A-Za-z0-9_.-]+) = "(.*)"$""")]
    private static partial Regex PropertyRegex();

    // "Cookie: 3922:700c" in `pactl info`. PulseAudio and pipewire-pulse pick a new
    // cookie on every server start, which makes it the server-instance token.
    [GeneratedRegex(@"^Cookie:\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ServerCookieRegex();

    private readonly IProcessRunner _processRunner;
    private readonly IErrorLogService _errorLog;
    private readonly string? _journalPath;
    private readonly IDefaultDeviceChangeWatcher? _newStreamWatcher;
    private readonly string _ownProcessId;
    private readonly Func<string, string?> _parentProcessId;
    // Taken before _volumesGate, never inside it, so journal writes land in snapshot order.
    private readonly Lock _journalGate = new();
    // Guarded by _volumesGate, mirroring MediaPauseService's _playersGate:
    // RestoreAudio is reachable from async continuations and Dispose, so two
    // threads can otherwise enumerate/mutate the dictionary concurrently.
    // pactl round trips stay OUTSIDE the gate; only a duck's set-volume is LAUNCHED inside it.
    private readonly Dictionary<string, SavedVolumeState> _savedVolumes =
        new(StringComparer.Ordinal);
    // Streams lowered by an earlier recording or run that were gone before their restore.
    // The server hands their remembered (ducked) volume to the app's next stream.
    private readonly List<JournalEntry> _pendingRestores = [];
    // Journaled by the running pass before its set-volumes launch.
    private readonly List<JournalEntry> _plannedDucks = [];
    private readonly Lock _volumesGate = new();
    // _isDucked stays false until a duck COMPLETES: RestoreAudio must not run while a
    // duck is mid-flight, or a restore could put an original volume back and remove
    // the entry just before the duck's queued set-volume lands — leaving the stream
    // ducked with nothing tracking it. _duckInProgress separately blocks duck
    // re-entry so the two guards can't be satisfied by the same half-open state.
    private bool _duckInProgress;
    // Between DuckAudio and RestoreAudio: streams that start in between are lowered too.
    private bool _sessionActive;
    private float _sessionFactor;
    // Another pass was asked for while one ran; the running pass runs it next.
    private bool _passRequested;
    // A restore that arrived mid-duck is deferred, not dropped: the duck's finally runs
    // it once the last set-volume has landed. Dropping it would leave every stream the
    // duck just lowered quiet forever when shutdown or session loss races the duck.
    private bool _restorePending;
    private bool _isDucked;
    // Set by RestoreAudioForShutdown under the gate; no duck set-volume starts after it.
    private bool _shutDown;
    // The running duck set-volume, registered in the critical section that launched it.
    private InFlightDuck? _inFlightDuck;

    public AudioDuckingService(
        IProcessRunner processRunner,
        IErrorLogService errorLog,
        SystemCommandAvailabilityService commands
    )
        : this(
            processRunner,
            errorLog,
            Path.Join(TypeWhisperEnvironment.DataPath, JournalFileName),
            new PactlDefaultDeviceWatcher(
                commands,
                processRunner,
                IsNewSinkInputEvent,
                s_newStreamDebounce
            )
        )
    {
    }

    // A null journal path turns journaling off; only tests do that.
    internal AudioDuckingService(
        IProcessRunner processRunner,
        IErrorLogService errorLog,
        string? journalPath = null,
        IDefaultDeviceChangeWatcher? newStreamWatcher = null,
        int? ownProcessId = null,
        Func<string, string?>? parentProcessId = null
    )
    {
        _processRunner = processRunner;
        _errorLog = errorLog;
        _journalPath = journalPath;
        _newStreamWatcher = newStreamWatcher;
        _ownProcessId = (ownProcessId ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture);
        _parentProcessId = parentProcessId ?? ReadParentProcessId;

        if (!LoadJournal())
        {
            return;
        }

        EnsureWatchingNewStreams();
        StartupRepair = Task.Run(RequestPass);
    }

    // Repairs what an interrupted earlier run left lowered.
    internal Task StartupRepair { get; } = Task.CompletedTask;

    // Test seam: runs just before RestoreAudioForShutdown waits for an in-flight set-volume.
    internal Action? ShutdownWaitStarting { get; init; }

    // Test seam: runs under the gate, after the shutdown check and before the set-volume launches.
    internal Action? DuckCommandLaunching { get; init; }

    // Test seam: runs after a set-volume exits, before the duck settles it.
    internal Action? DuckCommandExited { get; init; }

    internal static bool IsNewSinkInputEvent(string? line)
    {
        return line?.StartsWith("Event 'new' on sink-input #", StringComparison.Ordinal) == true;
    }

    public void DuckAudio(float factor)
    {
        lock (_volumesGate)
        {
            if (_shutDown || _sessionActive)
            {
                return;
            }

            if (_duckInProgress)
            {
                // A repair or a previous recording's pass is running; it ducks for this
                // recording once it ends. Its deferred restore is dropped: the streams it
                // would restore stay lowered for this recording and are restored with it.
                _sessionActive = true;
                _sessionFactor = factor;
                _restorePending = false;
                _passRequested = true;
                return;
            }

            // A failed restore still holds original volumes; ducking over it would save
            // already-lowered ones.
            if (_isDucked)
            {
                return;
            }

            _sessionActive = true;
            _sessionFactor = factor;
            _duckInProgress = true;
        }

        EnsureWatchingNewStreams();
        RunPasses();
    }

    public void RestoreAudioForShutdown(TimeSpan settleTimeout)
    {
        InFlightDuck? inFlightDuck;
        lock (_volumesGate)
        {
            _shutDown = true;
            _sessionActive = false;
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
                // Restored without it; the duck undoes this set itself once it exits. The app may
                // exit first, so the journal keeps the stream for the next run to check.
                inFlightDuck.LandsAfterShutdownRestore = true;
                AddPendingRestoreLocked(
                    new JournalEntry(
                        inFlightDuck.Identity,
                        inFlightDuck.InputId,
                        inFlightDuck.ServerCookie,
                        inFlightDuck.OriginalVolumes,
                        inFlightDuck.DuckedVolumes
                    )
                );
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
            _sessionActive = false;
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

    // Start is a no-op while the watcher runs, and relaunches one that gave up reconnecting.
    private void EnsureWatchingNewStreams()
    {
        try
        {
            _newStreamWatcher?.Start(RequestPass);
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"[AudioDuckingService] Watching for new streams failed: {ex.Message}");
        }
    }

    // New stream or startup: lowers new streams while recording, repairs ones back at a ducked level.
    private void RequestPass()
    {
        lock (_volumesGate)
        {
            if (_shutDown || (!_sessionActive && _pendingRestores.Count == 0))
            {
                return;
            }

            if (_duckInProgress)
            {
                _passRequested = true;
                return;
            }

            _duckInProgress = true;
        }

        RunPasses();
    }

    // Caller set _duckInProgress. Runs passes until none is requested, then the deferred restore.
    private void RunPasses()
    {
        while (true)
        {
            try
            {
                RunPass();
            }
            catch (Exception ex)
            {
                // Entries saved before the failure are kept: they hold the only copy of the
                // original volumes for streams already ducked, and RestoreAudio needs them.
                Debug.WriteLine($"[AudioDuckingService] Duck failed: {ex.Message}");
            }

            bool restoreDeferred;
            lock (_volumesGate)
            {
                _plannedDucks.Clear();
                _isDucked = _savedVolumes.Count > 0;
                if (!_shutDown && _passRequested)
                {
                    _passRequested = false;
                    continue;
                }

                _passRequested = false;
                _duckInProgress = false;
                // Shutdown restores everything itself and never defers to the duck.
                restoreDeferred = !_shutDown && _restorePending && _isDucked;
                _restorePending = false;
            }

            // Outside the gate: the restore retakes it and runs pactl round trips.
            if (restoreDeferred)
            {
                RestoreSavedVolumes();
            }

            return;
        }
    }

    private void RunPass()
    {
        // The cookie must read the same on both sides of the listing: a restart in
        // between would pair one server's volumes with another server's ids.
        var serverCookie = ReadServerCookie();
        if (serverCookie is null || IsShutDown())
        {
            return;
        }

        var sinkInputs = ListSinkInputs();
        if (sinkInputs is null || sinkInputs.Count == 0 || IsShutDown())
        {
            return;
        }

        if (!string.Equals(ReadServerCookie(), serverCookie, StringComparison.Ordinal))
        {
            return;
        }

        RepairRememberedVolumes(sinkInputs, serverCookie);
        DuckUntrackedStreams(sinkInputs, serverCookie);
    }

    private void RepairRememberedVolumes(List<SinkInput> sinkInputs, string serverCookie)
    {
        List<JournalEntry> pending;
        lock (_volumesGate)
        {
            pending = [.. _pendingRestores];
        }

        // A live stream with its own entry is never claimed by another stream's entry.
        var ownedIds = pending
            .Where(entry => entry.ServerCookie == serverCookie)
            .Select(entry => entry.InputId)
            .ToHashSet(StringComparer.Ordinal);
        // A repaired stream is never repaired again by another entry: its restored volume can
        // equal that entry's ducked level, and the server now remembers the restored one.
        var repairedIds = new HashSet<string>(StringComparer.Ordinal);

        var journalChanged = false;
        foreach (var entry in pending)
        {
            var allRepaired = true;
            var anyMatched = false;
            var streamAlive = entry.ServerCookie == serverCookie
                && sinkInputs.Exists(input => input.Id == entry.InputId);
            for (var index = 0; index < sinkInputs.Count; index++)
            {
                var input = sinkInputs[index];
                // The stream itself, or another stream of the same app that has no entry of its
                // own (a sibling that inherited the ducked level is covered by this entry).
                var candidate = (streamAlive && input.Id == entry.InputId)
                    || (!ownedIds.Contains(input.Id) && entry.Matches(input, serverCookie));
                if (!candidate)
                {
                    continue;
                }

                if (repairedIds.Contains(input.Id))
                {
                    anyMatched = true;
                    continue;
                }

                bool track;
                lock (_volumesGate)
                {
                    if (_shutDown)
                    {
                        return;
                    }

                    // Tracked by this recording, which restores it; its app's remembered level is ours again.
                    anyMatched = true;
                    if (_savedVolumes.ContainsKey(input.Id))
                    {
                        continue;
                    }

                    // At any other level the volume was set after the duck, so it is not ours to change.
                    if (!VolumesMatch(input.Volumes, entry.Ducked))
                    {
                        continue;
                    }

                    // During a recording the stream is already where the duck wants it.
                    track = _sessionActive;
                    if (track)
                    {
                        _savedVolumes.Add(
                            input.Id,
                            new SavedVolumeState(entry.Original, input.Volumes, input.Identity, serverCookie, 0)
                        );
                    }
                }

                if (track)
                {
                    continue;
                }

                var result = SetSinkInputVolume(input.Id, entry.Original);
                if (result.Succeeded)
                {
                    repairedIds.Add(input.Id);
                    sinkInputs[index] = input with { Volumes = entry.Original };
                    WriteDiagnostic(
                        $"[AudioDuckingService] Restored sink input {input.Id} from a level an earlier duck left behind."
                    );
                }
                else
                {
                    allRepaired = false;
                }
            }

            // An entry whose id is gone and that has no identity can never match again.
            var unreachable = !anyMatched && !streamAlive && entry.Identity is null;
            if (!(anyMatched && allRepaired) && !unreachable)
            {
                continue;
            }

            lock (_volumesGate)
            {
                _pendingRestores.Remove(entry);
            }

            journalChanged = true;
        }

        if (journalChanged)
        {
            WriteJournal();
        }
    }

    private void DuckUntrackedStreams(List<SinkInput> sinkInputs, string serverCookie)
    {
        List<JournalEntry> planned = [];
        var inherited = false;
        lock (_volumesGate)
        {
            if (_shutDown || !_sessionActive)
            {
                return;
            }

            var factor = _sessionFactor;
            foreach (var input in sinkInputs)
            {
                if (_savedVolumes.ContainsKey(input.Id))
                {
                    continue;
                }

                // A ducked app's new stream starts at the level the server remembered from
                // the duck; lowering it again would save that level as its original.
                var sibling = input.Identity is null
                    ? null
                    : _savedVolumes.Values.FirstOrDefault(state =>
                        state.Identity == input.Identity
                        && state.ServerCookie == serverCookie
                        && VolumesMatch(input.Volumes, state.Ducked)
                    );
                if (sibling is not null)
                {
                    _savedVolumes.Add(
                        input.Id,
                        new SavedVolumeState(sibling.Volumes, input.Volumes, input.Identity, serverCookie, 0)
                    );
                    inherited = true;
                    continue;
                }

                planned.Add(
                    new JournalEntry(
                        input.Identity,
                        input.Id,
                        serverCookie,
                        input.Volumes,
                        [.. input.Volumes.Select(volume => ScaleVolume(volume, factor))]
                    )
                );
            }

            _plannedDucks.AddRange(planned);
        }

        if (planned.Count == 0)
        {
            if (inherited)
            {
                WriteJournal();
            }

            return;
        }

        // Nothing is lowered unless the journal holds its original volume first.
        if (!WriteJournal())
        {
            return;
        }

        foreach (var plan in planned)
        {
            InFlightDuck duck;
            lock (_volumesGate)
            {
                // The check, the save and the launch share one critical section, so a
                // shutdown either refuses this set or finds it registered and waits for it.
                if (_shutDown || !_sessionActive)
                {
                    return;
                }

                // Never overwrite: an entry that survived a failed restore still
                // holds the ORIGINAL volume — replacing it here would capture the
                // already-ducked value and lower that stream permanently.
                if (!_savedVolumes.TryGetValue(plan.InputId, out var savedState))
                {
                    savedState = new SavedVolumeState(plan.Original, plan.Ducked, plan.Identity, serverCookie, 0);
                    _savedVolumes.Add(plan.InputId, savedState);
                }

                _plannedDucks.Remove(plan);
                DuckCommandLaunching?.Invoke();
                // RunAsync returns once pactl has started, so only the launch holds the gate.
                duck = _inFlightDuck = new InFlightDuck(
                    plan.InputId,
                    savedState.Identity,
                    savedState.Volumes,
                    plan.Ducked,
                    savedState.ServerCookie,
                    StartSetSinkInputVolume(plan.InputId, plan.Ducked)
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

    private bool IsShutDown()
    {
        lock (_volumesGate)
        {
            return _shutDown;
        }
    }

    // Caller holds _volumesGate and the set has finished. Returns true when it may have lowered
    // the stream after shutdown's restore started, so the caller must undo it.
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

        // A timed-out set may still have landed: the server can apply it before pactl reads the reply.
        return duck is
        {
            LandsAfterShutdownRestore: true,
            Command:
            {
                IsCompletedSuccessfully: true,
                Result: { Succeeded: true } or { Started: true, TimedOut: true },
            },
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
        // Null when the listing fails; the restore then runs without the volume check.
        var current = currentCookie is null
            ? null
            : ListSinkInputs()?.ToDictionary(input => input.Id, StringComparer.Ordinal);
        var retired = false;

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
                    retired |= RetireUntilStreamReturns(inputId, state);
                    continue;
                }

                if (current is not null)
                {
                    if (!current.TryGetValue(inputId, out var input))
                    {
                        retired |= RetireUntilStreamReturns(inputId, state);
                        continue;
                    }

                    if (!VolumesMatch(input.Volumes, state.Ducked) && !VolumesMatch(input.Volumes, state.Volumes))
                    {
                        WriteDiagnostic(
                            $"[AudioDuckingService] Sink input {inputId} volume changed while ducked; leaving it."
                        );
                        RemoveSavedVolume(inputId);
                        continue;
                    }
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
                    retired |= RetireUntilStreamReturns(inputId, state);
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

        WriteJournal();
        // The app's next stream may already be playing, and its new-stream event came
        // before its entry was pending.
        if (retired)
        {
            RequestPass();
        }
    }

    private void RemoveSavedVolume(string inputId)
    {
        lock (_volumesGate)
        {
            _savedVolumes.Remove(inputId);
        }
    }

    // The stream is gone, but the server hands its ducked volume to the app's next stream.
    // Returns true when an entry now waits for that stream.
    private bool RetireUntilStreamReturns(string inputId, SavedVolumeState state)
    {
        WriteDiagnostic(
            $"[AudioDuckingService] Sink input {inputId} vanished; restoring its app's next stream instead."
        );
        lock (_volumesGate)
        {
            if (!_savedVolumes.Remove(inputId) || state.Identity is null)
            {
                return false;
            }

            AddPendingRestoreLocked(
                new JournalEntry(state.Identity, inputId, state.ServerCookie, state.Volumes, state.Ducked)
            );
        }

        EnsureWatchingNewStreams();
        return true;
    }

    // Caller holds _volumesGate.
    private void AddPendingRestoreLocked(JournalEntry entry)
    {
        _pendingRestores.RemoveAll(pending =>
            pending.ServerCookie == entry.ServerCookie && pending.InputId == entry.InputId
        );
        _pendingRestores.Add(entry);
    }

    public void Dispose()
    {
        RestoreAudio();
        _newStreamWatcher?.Dispose();
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
                // The stream may still be lowered; the journal keeps it for a later check.
                _savedVolumes.Remove(inputId);
                AddPendingRestoreLocked(
                    new JournalEntry(current.Identity, inputId, current.ServerCookie, current.Volumes, current.Ducked)
                );
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

    // Returns false when there was nothing to repair.
    private bool LoadJournal()
    {
        if (_journalPath is null || !File.Exists(_journalPath))
        {
            return false;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<JournalEntry>>(
                File.ReadAllText(_journalPath),
                s_journalJsonOptions
            );
            lock (_volumesGate)
            {
                foreach (var entry in entries ?? [])
                {
                    // A hand-edited or damaged journal deserializes nulls into these non-nullable members.
                    // ReSharper disable RedundantAlwaysMatchSubpattern
                    if (entry is { InputId: not null, ServerCookie: not null, Original.Length: > 0, Ducked.Length: > 0 })
                    // ReSharper restore RedundantAlwaysMatchSubpattern
                    {
                        AddPendingRestoreLocked(entry);
                    }
                }

                return _pendingRestores.Count > 0;
            }
        }
        catch (Exception ex)
        {
            ReportRestoreFailure($"Could not read the audio ducking journal: {ex.Message}");
            return false;
        }
    }

    private bool WriteJournal()
    {
        if (_journalPath is null)
        {
            return true;
        }

        lock (_journalGate)
        {
            List<JournalEntry> entries;
            lock (_volumesGate)
            {
                entries =
                [
                    .. _savedVolumes.Select(saved => new JournalEntry(
                        saved.Value.Identity,
                        saved.Key,
                        saved.Value.ServerCookie,
                        saved.Value.Volumes,
                        saved.Value.Ducked
                    )),
                    .. _plannedDucks,
                    .. _pendingRestores,
                ];
            }

            try
            {
                if (entries.Count == 0)
                {
                    File.Delete(_journalPath);
                    return true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
                AtomicFileWrite.WriteAllText(_journalPath, JsonSerializer.Serialize(entries, s_journalJsonOptions));
                return true;
            }
            catch (Exception ex)
            {
                ReportRestoreFailure($"Could not write the audio ducking journal, so nothing was lowered: {ex.Message}");
                return false;
            }
        }
    }

    // Null when pactl fails, so callers can tell "no streams" from "could not look".
    private List<SinkInput>? ListSinkInputs()
    {
        // pactl has no "get-sink-input-volume" subcommand, so read current
        // volumes by parsing the long `list sink-inputs` output instead.
        var result = RunPactl(["list", "sink-inputs"]);
        if (!result.Succeeded)
        {
            return null;
        }

        return [.. ParseSinkInputs(result.StandardOutput).Where(input => !IsOwnPlayback(input))];
    }

    // TypeWhisper plays in-process and through helpers it launches (paplay/pw-play for Read Aloud).
    private bool IsOwnPlayback(SinkInput input)
    {
        return input.ProcessId is { } processId
            && (processId == _ownProcessId || _parentProcessId(processId) == _ownProcessId);
    }

    private static string? ReadParentProcessId(string processId)
    {
        try
        {
            // "pid (comm) state ppid ...": comm may contain spaces, so read after the last ')'.
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 ? fields[1] : null;
        }
        catch (Exception)
        {
            // Gone, another user's process, or no /proc: not ours to skip.
            return null;
        }
    }

    /// <summary>
    ///     Walks the <c>pactl list sink-inputs</c> output, reading each "Sink Input #N" block's
    ///     raw channel volumes from its first "Volume:" line, plus the properties that identify it.
    /// </summary>
    private static IEnumerable<SinkInput> ParseSinkInputs(string listing)
    {
        string? currentId = null;
        string[]? volumes = null;
        string? processId = null;
        string? identity = null;

        foreach (var line in listing.Split('\n').Select(raw => raw.Trim()))
        {
            var idMatch = SinkInputIdRegex().Match(line);
            if (idMatch.Success)
            {
                if (currentId is not null && volumes is not null)
                {
                    yield return new SinkInput(currentId, volumes, processId, identity);
                }

                currentId = idMatch.Groups[1].Value;
                volumes = null;
                processId = null;
                identity = null;
                continue;
            }

            if (currentId is null)
            {
                continue;
            }

            if (volumes is null && line.StartsWith("Volume:", StringComparison.Ordinal))
            {
                var parsed = RawVolumeRegex()
                    .Matches(line)
                    .Select(match => match.Groups[1].Value)
                    .ToArray();
                if (parsed.Length > 0)
                {
                    volumes = parsed;
                }

                continue;
            }

            var property = PropertyRegex().Match(line);
            if (!property.Success)
            {
                continue;
            }

            switch (property.Groups[1].Value)
            {
                case "application.process.id":
                    processId = property.Groups[2].Value;
                    break;
                // The key the server remembers this stream's volume under.
                case "module-stream-restore.id":
                    identity = property.Groups[2].Value;
                    break;
            }
        }

        if (currentId is not null && volumes is not null)
        {
            yield return new SinkInput(currentId, volumes, processId, identity);
        }
    }

    private static bool VolumesMatch(string[] current, string[] expected)
    {
        if (current.Length != expected.Length)
        {
            return false;
        }

        for (var index = 0; index < current.Length; index++)
        {
            if (
                !ulong.TryParse(current[index], NumberStyles.None, CultureInfo.InvariantCulture, out var actual)
                || !ulong.TryParse(expected[index], NumberStyles.None, CultureInfo.InvariantCulture, out var wanted)
                || (actual > wanted ? actual - wanted : wanted - actual) > RawVolumeTolerance
            )
            {
                return false;
            }
        }

        return true;
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

    private sealed record SinkInput(string Id, string[] Volumes, string? ProcessId, string? Identity);

    // Volumes holds the ORIGINAL volume; Ducked is what the duck set.
    private sealed record SavedVolumeState(
        string[] Volumes,
        string[] Ducked,
        string? Identity,
        string ServerCookie,
        int FailedRestoreAttempts
    );

    // Identity is the server's stream-restore key, which outlives the sink input id and the server run.
    private sealed record JournalEntry(
        string? Identity,
        string InputId,
        string ServerCookie,
        string[] Original,
        string[] Ducked
    )
    {
        public bool Matches(SinkInput input, string serverCookie)
        {
            return (ServerCookie == serverCookie && InputId == input.Id)
                || (Identity is not null && Identity == input.Identity);
        }
    }

    // Settled and LandsAfterShutdownRestore are guarded by _volumesGate.
    private sealed class InFlightDuck(
        string inputId,
        string? identity,
        string[] originalVolumes,
        string[] duckedVolumes,
        string serverCookie,
        Task<ProcessRunResult> command
    )
    {
        public string InputId { get; } = inputId;
        public string? Identity { get; } = identity;
        public string[] OriginalVolumes { get; } = originalVolumes;
        public string[] DuckedVolumes { get; } = duckedVolumes;
        public string ServerCookie { get; } = serverCookie;
        public Task<ProcessRunResult> Command { get; } = command;
        public bool Settled { get; set; }
        public bool LandsAfterShutdownRestore { get; set; }
    }
}
