using SharpHook.Native;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Reads keyboard input directly from <c>/dev/input/event*</c> for global
///     hotkey detection on Wayland (SharpHook only sees events while focused).
///     Requires read access to a keyboard event node — granted on first run by
///     the keyboard-access udev rule (<see cref="InputAccessSetupHelper" />) via
///     <c>TAG+="uaccess"</c>, with <c>GROUP="input"</c> as the non-logind
///     fallback. <see cref="IsAvailable" /> returns false when no device can be
///     opened so callers fall through to SharpHook.
/// </summary>
public sealed class EvdevGlobalShortcutBackend : IGlobalShortcutBackend
{
    private const string BackendId = "linux-evdev";
    private const string InputDir = "/dev/input";

    // Belt-and-suspenders rescan: FileSystemWatcher can miss events under high I/O load.
    // 30 s catches a late-plugged USB keyboard without hammering the kernel.
    private static readonly TimeSpan s_rescanInterval = TimeSpan.FromSeconds(30);

    private readonly IEvdevKeyboardEnumerator _deviceEnumerator;
    private readonly IEvdevDeviceReaderFactory _deviceReaderFactory;
    private readonly ShortcutDispatcher _dispatcher = new();
    private readonly bool _enableDeviceMonitoring;
    private readonly Lock _lock = new();
    private readonly bool _ownsSessionActivityMonitor;
    private readonly Action? _beforeSessionResetState;
    private readonly Dictionary<int, int> _aggregateKeyCounts = new();
    private readonly HashSet<TaskCompletionSource<bool>> _outstandingResetBarriers = [];
    private readonly Dictionary<string, HashSet<int>> _pressedKeysByDevice = new();
    private readonly Dictionary<string, IEvdevDeviceReader> _readers = new();
    private readonly ISessionActivityMonitor _sessionActivityMonitor;
    private int _disposed;

    private long _lifecycleGeneration;
    private CancellationTokenSource? _rescanCts;
    private bool _inputAllowed = true;
    private bool _started;

    private FileSystemWatcher? _watcher;

    public EvdevGlobalShortcutBackend()
        : this(
            new LogindSessionActivityMonitor(),
            new EvdevKeyboardEnumerator(),
            new EvdevDeviceReaderFactory(),
            true,
            true,
            null
        )
    {
    }

    public EvdevGlobalShortcutBackend(ISessionActivityMonitor sessionActivityMonitor)
        : this(
            sessionActivityMonitor,
            new EvdevKeyboardEnumerator(),
            new EvdevDeviceReaderFactory(),
            true,
            false,
            null
        )
    {
    }

    internal EvdevGlobalShortcutBackend(
        ISessionActivityMonitor sessionActivityMonitor,
        IEvdevKeyboardEnumerator deviceEnumerator,
        IEvdevDeviceReaderFactory deviceReaderFactory,
        bool enableDeviceMonitoring = false,
        Action? beforeSessionResetState = null
    )
        : this(
            sessionActivityMonitor,
            deviceEnumerator,
            deviceReaderFactory,
            enableDeviceMonitoring,
            false,
            beforeSessionResetState
        )
    {
    }

    private EvdevGlobalShortcutBackend(
        ISessionActivityMonitor sessionActivityMonitor,
        IEvdevKeyboardEnumerator deviceEnumerator,
        IEvdevDeviceReaderFactory deviceReaderFactory,
        bool enableDeviceMonitoring,
        bool ownsSessionActivityMonitor,
        Action? beforeSessionResetState
    )
    {
        _sessionActivityMonitor = sessionActivityMonitor;
        _deviceEnumerator = deviceEnumerator;
        _deviceReaderFactory = deviceReaderFactory;
        _enableDeviceMonitoring = enableDeviceMonitoring;
        _ownsSessionActivityMonitor = ownsSessionActivityMonitor;
        _beforeSessionResetState = beforeSessionResetState;
        _sessionActivityMonitor.InputAllowedChanged += OnInputAllowedChanged;

        _dispatcher.DictationToggleRequested += () =>
            DictationToggleRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStartRequested += () =>
            DictationStartRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStopRequested += () =>
            DictationStopRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationDiscardRequested += () =>
            DictationDiscardRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.PromptPaletteRequested += () =>
            PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.TransformSelectionRequested += () =>
            TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.RecentTranscriptionsRequested += () =>
            RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CopyLastTranscriptionRequested += () =>
            CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CancelRequested += () => CancelRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.PromptActionRequested += actionId =>
            PromptActionRequested?.Invoke(this, actionId);
        _dispatcher.ProfileDictationToggleRequested += id =>
            ProfileDictationToggleRequested?.Invoke(this, id);
        _dispatcher.ProfileDictationStartRequested += id =>
            ProfileDictationStartRequested?.Invoke(this, id);
        _dispatcher.ProfileDictationStopRequested += () =>
            ProfileDictationStopRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.ProfileTextProcessingRequested += id =>
            ProfileTextProcessingRequested?.Invoke(this, id);
    }

    public string Id => BackendId;
    public string DisplayName => "Linux evdev";
    public bool SupportsPressRelease => true;
    public bool IsGlobalScope => true;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? DictationDiscardRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? PromptActionRequested;
    public event EventHandler<string>? ProfileDictationToggleRequested;
    public event EventHandler<string>? ProfileDictationStartRequested;
    public event EventHandler? ProfileDictationStopRequested;
    public event EventHandler<string>? ProfileTextProcessingRequested;
    public event EventHandler<string>? Failed;

    public bool IsAvailable()
    {
        // Available when at least one keyboard event node is readable — the
        // ground-truth probe shared with the keyboard-access setup task.
        return InputDeviceAccessCheck.HasKeyboardAccess();
    }

    public async Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    )
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return new GlobalShortcutRegistrationResult(
                false,
                BackendId,
                "evdev backend is disposed.",
                false,
                null
            );
        }

        _dispatcher.UpdateShortcuts(shortcuts);
        await _sessionActivityMonitor.InitializeAsync(ct).ConfigureAwait(false);

        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return new GlobalShortcutRegistrationResult(
                    false,
                    BackendId,
                    "evdev backend is disposed.",
                    false,
                    null
                );
            }

            // ReSharper disable once InvertIf — last statement in the lock; inverting would
            // duplicate the trailing success-result construction with no clean early exit.
            if (!_started)
            {
                _inputAllowed = _sessionActivityMonitor.IsInputAllowed;
                _lifecycleGeneration++;
                if (_inputAllowed)
                {
                    AttachAllDevices_NoLock(_lifecycleGeneration);
                }

                StartHotPlugWatcher_NoLock();
                StartPeriodicRescan_NoLock();
                _started = true;

                if (_inputAllowed && _readers.Count == 0)
                {
                    return new GlobalShortcutRegistrationResult(
                        false,
                        BackendId,
                        Loc.Instance["Shortcuts.EvdevNoKeyboardAccess"],
                        false,
                        InputAccessSetupHelper.ManualInstallCommand()
                    );
                }
            }
        }

        return new GlobalShortcutRegistrationResult(
            true,
            BackendId,
            null,
            false,
            null
        );
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        _dispatcher.ClearShortcuts();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _sessionActivityMonitor.InputAllowedChanged -= OnInputAllowedChanged;

        FileSystemWatcher? watcher;
        CancellationTokenSource? rescan;
        List<IEvdevDeviceReader> readers;
        lock (_lock)
        {
            _lifecycleGeneration++;
            _inputAllowed = false;
            watcher = _watcher;
            _watcher = null;
            rescan = _rescanCts;
            _rescanCts = null;
            readers = _readers.Values.ToList();
            _readers.Clear();
            ClearInputState_NoLock();
            // A queued reopen may be waiting for a session reset that another callback is still
            // finishing. Disposal supersedes that work; wake it so it can observe _disposed.
            foreach (var resetBarrier in _outstandingResetBarriers)
            {
                resetBarrier.TrySetResult(true);
            }

            _outstandingResetBarriers.Clear();
        }

        // Whole-input teardown is the security boundary: clear dispatcher guards and discard any
        // recording exactly once, outside the backend lock so event handlers cannot invert locks.
        _dispatcher.ResetState();

        try
        {
            watcher?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevBackend] Watcher dispose threw: {ex.Message}");
        }

        if (rescan is not null)
        {
            try
            {
                await rescan.CancelAsync();
                rescan.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevBackend] Rescan dispose threw: {ex.Message}");
            }
        }

        await DisposeReadersAsync(readers).ConfigureAwait(false);

        if (_ownsSessionActivityMonitor)
        {
            try
            {
                await _sessionActivityMonitor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[EvdevBackend] Session activity monitor dispose threw: {ex.Message}"
                );
            }
        }
    }

    private void AttachAllDevices_NoLock(long generation)
    {
        foreach (var path in _deviceEnumerator.EnumerateKeyboards())
        {
            TryAttach_NoLock(path, generation);
        }
    }

    [SuppressMessage("Usage", "CA2012:Use ValueTasks correctly", Justification = "Intentional fire-and-forget disposal of a reader that failed to start; EvdevDeviceReader.DisposeAsync is a self-contained async ValueTask and awaiting here would needlessly block the attach path.")]
    private void TryAttach_NoLock(string path, long generation)
    {
        if (
            !_inputAllowed
            || generation != _lifecycleGeneration
            || Volatile.Read(ref _disposed) == 1
            || _readers.ContainsKey(path)
        )
        {
            return;
        }

        var reader = _deviceReaderFactory.Create(
            path,
            (devicePath, linuxKeyCode, pressed) =>
                OnKeyEvent(generation, devicePath, linuxKeyCode, pressed),
            (devicePath, exception) => OnReaderFailure(generation, devicePath, exception)
        );
        // Before TryStart: a fast first event then waits on this lock and sees fully attached state.
        _readers[path] = reader;
        _pressedKeysByDevice[path] = [];
        if (reader.TryStart())
        {
            Trace.WriteLine($"[EvdevBackend] Attached {path}");
        }
        else
        {
            _readers.Remove(path);
            _pressedKeysByDevice.Remove(path);
            _ = reader.DisposeAsync();
        }
    }

    private void StartHotPlugWatcher_NoLock()
    {
        if (!_enableDeviceMonitoring || _watcher is not null)
        {
            return;
        }

        if (!Directory.Exists(InputDir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(InputDir, "event*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            _watcher.Created += OnDeviceCreated;
            _watcher.Deleted += OnDeviceDeleted;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevBackend] FileSystemWatcher failed: {ex.Message}");
        }
    }

    private void StartPeriodicRescan_NoLock()
    {
        if (!_enableDeviceMonitoring || _rescanCts is not null)
        {
            return;
        }

        _rescanCts = new CancellationTokenSource();
        var ct = _rescanCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(s_rescanInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Rescan();
            }
        }, ct);
    }

    private void OnDeviceCreated(object? sender, FileSystemEventArgs e)
    {
        // Kernel creates /dev/input/eventN before its by-path symlink resolves — retry with backoff.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                if (Rescan())
                {
                    return;
                }
            }
        });
    }

    [SuppressMessage("Usage", "CA2012:Use ValueTasks correctly", Justification = "Intentional fire-and-forget disposal of a removed reader; EvdevDeviceReader.DisposeAsync is a self-contained async ValueTask and must not block this FileSystemWatcher callback.")]
    private void OnDeviceDeleted(object? sender, FileSystemEventArgs e)
    {
        IEvdevDeviceReader? reader;
        List<DispatchEdge> releases;
        lock (_lock)
        {
            releases = DetachDevice_NoLock(e.FullPath, out reader);
        }

        if (reader is not null)
        {
            _ = reader.DisposeAsync();
        }

        DispatchEdges(releases);
    }

    [SuppressMessage("Usage", "CA2012:Use ValueTasks correctly", Justification = "Intentional fire-and-forget disposal of stale readers pruned during rescan; EvdevDeviceReader.DisposeAsync is a self-contained async ValueTask and awaiting here is unnecessary.")]
    internal bool Rescan()
    {
        var added = false;
        List<IEvdevDeviceReader>? toDispose = null;
        List<DispatchEdge>? releases = null;
        lock (_lock)
        {
            if (
                Volatile.Read(ref _disposed) == 1
                || !_inputAllowed
                || _outstandingResetBarriers.Count > 0
            )
            {
                return false;
            }

            var generation = _lifecycleGeneration;

            // Prune readers for paths that vanished — guards against FSW dropping Delete events under load.
            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- loop mutates _readers and builds toDispose; a LINQ rewrite would obscure the side effects
            foreach (var existing in _readers.Keys.Order(StringComparer.Ordinal).ToList())
            {
                if (_deviceEnumerator.Exists(existing))
                {
                    continue;
                }

                var detachedReleases = DetachDevice_NoLock(existing, out var stale);
                if (stale is null)
                {
                    continue;
                }

                toDispose ??= [];
                toDispose.Add(stale);
                if (detachedReleases.Count == 0)
                {
                    continue;
                }

                releases ??= [];
                releases.AddRange(detachedReleases);
            }

            foreach (var path in _deviceEnumerator.EnumerateKeyboards())
            {
                if (_readers.ContainsKey(path))
                {
                    continue;
                }

                TryAttach_NoLock(path, generation);
                added = _readers.ContainsKey(path) || added;
            }
        }

        if (toDispose is null)
        {
            return added;
        }

        foreach (var r in toDispose)
        {
            _ = r.DisposeAsync();
        }

        if (releases is not null)
        {
            DispatchEdges(releases);
        }

        return added;
    }

    private void OnKeyEvent(
        long generation,
        string devicePath,
        int linuxKeyCode,
        bool pressed
    )
    {
        DispatchEdge dispatchEdge;
        lock (_lock)
        {
            // Reader callbacks can already be queued when a session transition closes the fd.
            // Serialize the allowed/generation check with that transition so a stale callback
            // cannot update state or dispatch a shortcut after lock handling has completed. Also
            // consult the monitor directly: a callback can win this lock before OnInputAllowedChanged
            // has flipped the cached _inputAllowed, so the cache alone can be stale-open.
            if (
                Volatile.Read(ref _disposed) == 1
                || !_inputAllowed
                || !_sessionActivityMonitor.IsInputAllowed
                || generation != _lifecycleGeneration
                || !_readers.ContainsKey(devicePath)
                || !_pressedKeysByDevice.TryGetValue(devicePath, out var deviceKeys)
            )
            {
                return;
            }

            if (!TryApplyDeviceEdge_NoLock(deviceKeys, linuxKeyCode, pressed, out dispatchEdge))
            {
                return;
            }
        }

        // Dispatch OUTSIDE the backend lock. A shortcut handler runs synchronously up to its first
        // await (e.g. StartAsync touches HotkeyService._lock); holding _lock across that would invert
        // the lock order against a concurrent settings push (HotkeyService._lock → RegisterAsync →
        // backend _lock), an AB/BA deadlock. The dispatcher serializes its own state internally.
        //
        // Re-check input allowance here: between releasing the lock and dispatching, a lock
        // transition can advance the generation and reset the dispatcher. Dictation starts are also
        // gated in the orchestrator, but prompt-action/copy-last/transform-selection shortcuts are
        // not, so this stops any of them from firing after the session has locked.
        DispatchEdgeOutsideLock(dispatchEdge);
    }

    [SuppressMessage("Usage", "CA2012:Use ValueTasks correctly", Justification = "Intentional fire-and-forget disposal of the failed reader; EvdevDeviceReader.DisposeAsync is a self-contained async ValueTask and must not block this failure callback.")]
    private void OnReaderFailure(long generation, string path, Exception ex)
    {
        Trace.WriteLine($"[EvdevBackend] Reader {path} failed: {ex.Message}");
        IEvdevDeviceReader? reader;
        List<DispatchEdge> releases;
        lock (_lock)
        {
            if (generation != _lifecycleGeneration || Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            releases = DetachDevice_NoLock(path, out reader);
        }

        if (reader is not null)
        {
            _ = reader.DisposeAsync();
        }

        DispatchEdges(releases);
        Failed?.Invoke(this, $"Lost keyboard device {path}: {ex.Message}");
    }

    private void OnInputAllowedChanged(object? sender, EventArgs e)
    {
        List<IEvdevDeviceReader>? readers = null;
        TaskCompletionSource<bool>? resetBarrier = null;
        Task<bool>[] resetBarriers = [];
        long generation;
        var reopen = false;
        var resetState = false;

        lock (_lock)
        {
            // Read the monitor state under the backend lock: two racing change callbacks could
            // otherwise apply a stale pre-lock read last and leave the backend gated (or open)
            // against the monitor's current state.
            var allowed = _sessionActivityMonitor.IsInputAllowed;
            if (Volatile.Read(ref _disposed) == 1 || _inputAllowed == allowed)
            {
                return;
            }

            _inputAllowed = allowed;
            generation = ++_lifecycleGeneration;
            if (!allowed)
            {
                resetBarrier = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _outstandingResetBarriers.Add(resetBarrier);
                readers = _readers.Values.ToList();
                _readers.Clear();
                ClearInputState_NoLock();
                resetState = true;
            }
            else
            {
                reopen = _started;
                resetBarriers = _outstandingResetBarriers
                    .Select(barrier => barrier.Task)
                    .ToArray();
            }
        }

        if (resetState)
        {
            // Session loss is a whole-input teardown, unlike one-device detach. Preserve its
            // discard semantics without invoking dispatcher event handlers under the backend lock:
            // doing so would invert locks with shortcut handlers. Outstanding barriers gate both
            // queued reopen and rescan attachments, and unlock awaits every reset from overlapping
            // cycles so a delayed reset cannot clear a fresh hold from a newly attached reader.
            try
            {
                _beforeSessionResetState?.Invoke();
                _dispatcher.ResetState();
            }
            finally
            {
                lock (_lock)
                {
                    resetBarrier!.TrySetResult(true);
                    _outstandingResetBarriers.Remove(resetBarrier);
                }
            }
        }

        if (readers is not null)
        {
            // Start every disposal now so each reader signals its native poll wakeup promptly;
            // one slow read-loop shutdown must not delay another reader's fd cleanup.
            _ = DisposeReadersAsync(readers);
        }

        if (reopen)
        {
            QueueReopen(generation, resetBarriers);
        }
    }

    private void QueueReopen(long generation, Task<bool>[] resetBarriers)
    {
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(resetBarriers).ConfigureAwait(false);

            List<string> paths;
            try
            {
                paths = _deviceEnumerator.EnumerateKeyboards().ToList();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevBackend] Re-enumeration failed: {ex.Message}");
                return;
            }

            bool readerless;
            lock (_lock)
            {
                if (
                    Volatile.Read(ref _disposed) == 1
                    || !_started
                    || !_inputAllowed
                    || generation != _lifecycleGeneration
                )
                {
                    return;
                }

                foreach (var path in paths)
                {
                    TryAttach_NoLock(path, generation);
                }

                readerless = _readers.Count == 0;
            }

            // Ending an unlock with no readers means evdev keyboard access is gone; surface it
            // rather than silently advertising a working backend whose shortcuts do nothing.
            if (readerless)
            {
                Failed?.Invoke(this, "evdev keyboard access lost; global shortcuts are inactive.");
            }
        });
    }

    private bool TryApplyDeviceEdge_NoLock(
        HashSet<int> deviceKeys,
        int linuxKeyCode,
        bool pressed,
        out DispatchEdge dispatchEdge
    )
    {
        dispatchEdge = default;
        if (pressed)
        {
            if (!deviceKeys.Add(linuxKeyCode))
            {
                return false;
            }

            _aggregateKeyCounts.TryGetValue(linuxKeyCode, out var previousCount);
            _aggregateKeyCounts[linuxKeyCode] = previousCount + 1;
            if (previousCount != 0)
            {
                return false;
            }
        }
        else
        {
            if (!deviceKeys.Remove(linuxKeyCode))
            {
                return false;
            }

            var previousCount = _aggregateKeyCounts[linuxKeyCode];
            if (previousCount > 1)
            {
                _aggregateKeyCounts[linuxKeyCode] = previousCount - 1;
                return false;
            }

            _aggregateKeyCounts.Remove(linuxKeyCode);
        }

        return TryCreateDispatchEdge_NoLock(linuxKeyCode, pressed, out dispatchEdge);
    }

    private List<DispatchEdge> DetachDevice_NoLock(
        string path,
        out IEvdevDeviceReader? reader
    )
    {
        if (!_readers.Remove(path, out reader))
        {
            return [];
        }

        // ReSharper disable once DuplicatedSequentialIfBodies -- deliberate detach ordering: the reader map must be removed (setting the reader out-param) before the pressed-key map; the two removals have distinct side effects and cannot be merged
        if (!_pressedKeysByDevice.Remove(path, out var deviceKeys))
        {
            return [];
        }

        var releases = new List<DispatchEdge>();
        foreach (
            var linuxKeyCode in deviceKeys
                .OrderBy(static code => LinuxKeyMap.IsModifier(code) ? 1 : 0)
                .ThenBy(static code => code)
        )
        {
            if (!_aggregateKeyCounts.TryGetValue(linuxKeyCode, out var previousCount))
            {
                continue;
            }

            if (previousCount > 1)
            {
                _aggregateKeyCounts[linuxKeyCode] = previousCount - 1;
                continue;
            }

            _aggregateKeyCounts.Remove(linuxKeyCode);
            if (TryCreateDispatchEdge_NoLock(linuxKeyCode, false, out var release))
            {
                releases.Add(release);
            }
        }

        return releases;
    }

    private bool TryCreateDispatchEdge_NoLock(
        int linuxKeyCode,
        bool pressed,
        out DispatchEdge dispatchEdge
    )
    {
        var sharpHookKey = LinuxKeyMap.ToSharpHook(linuxKeyCode);
        if (sharpHookKey is null)
        {
            dispatchEdge = default;
            return false;
        }

        var modBit = LinuxKeyMap.ToModifier(linuxKeyCode);
        var modifiers = CurrentModifiers_NoLock();
        if (modBit != ModifierMask.None)
        {
            // A modifier can itself be a trigger. Exclude its own bit so a no-other-modifiers
            // binding such as RightCtrl continues to match on both press and release.
            modifiers &= ~modBit;
        }

        dispatchEdge = new DispatchEdge(
            sharpHookKey.Value,
            modifiers,
            pressed,
            _lifecycleGeneration
        );
        return true;
    }

    private ModifierMask CurrentModifiers_NoLock()
    {
        var modifiers = ModifierMask.None;
        foreach (var (linuxKeyCode, count) in _aggregateKeyCounts)
        {
            if (count > 0)
            {
                modifiers |= LinuxKeyMap.ToModifier(linuxKeyCode);
            }
        }

        return modifiers;
    }

    private void ClearInputState_NoLock()
    {
        _pressedKeysByDevice.Clear();
        _aggregateKeyCounts.Clear();
    }

    private void DispatchEdges(IEnumerable<DispatchEdge> edges)
    {
        foreach (var edge in edges)
        {
            DispatchEdgeOutsideLock(edge);
        }
    }

    private void DispatchEdgeOutsideLock(DispatchEdge edge)
    {
        // These live checks close races between releasing _lock and calling dispatcher handlers.
        // They apply equally to physical events and synthetic detach releases.
        if (
            Volatile.Read(ref _disposed) == 1
            || !Volatile.Read(ref _inputAllowed)
            || Volatile.Read(ref _lifecycleGeneration) != edge.Generation
            || !_sessionActivityMonitor.IsInputAllowed
        )
        {
            return;
        }

        _dispatcher.Handle(edge.Key, edge.Modifiers, edge.Pressed);
    }

    private readonly record struct DispatchEdge(
        KeyCode Key,
        ModifierMask Modifiers,
        bool Pressed,
        long Generation
    );

    private static async Task DisposeReadersAsync(IEnumerable<IEvdevDeviceReader> readers)
    {
        var disposals = new List<Task>();
        foreach (var reader in readers)
        {
            try
            {
                disposals.Add(reader.DisposeAsync().AsTask());
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevBackend] Reader dispose threw: {ex.Message}");
            }
        }

        if (disposals.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(disposals).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevBackend] Reader dispose threw: {ex.Message}");
        }
    }
}
