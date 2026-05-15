using System.Diagnostics;
using System.IO;
using SharpHook.Native;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
/// Reads keyboard input directly from <c>/dev/input/event*</c>, allowing
/// global hotkey detection under Wayland sessions where the SharpHook /
/// libuiohook backend only sees events while the application owns focus.
///
/// Requires the running user to be in the <c>input</c> group (or an
/// equivalent udev rule). <see cref="IsAvailable"/> returns false when no
/// keyboard device can be opened — callers should fall through to the
/// SharpHook backend in that case.
/// </summary>
public sealed class EvdevGlobalShortcutBackend : IGlobalShortcutBackend
{
    public const string BackendId = "linux-evdev";
    private const string InputDir = "/dev/input";
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(30);

    private readonly ShortcutDispatcher _dispatcher = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, EvdevDeviceReader> _readers = new();

    // Aggregated modifier state across every attached keyboard. evdev gives
    // us individual key transitions; we must maintain the mask ourselves so
    // ShortcutMatcher can compare against the configured chord. Stored as
    // int and mutated through Interlocked.Or / Interlocked.And so two
    // reader tasks can update bits without racing on a read-modify-write.
    private int _liveModifiersBits;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _rescanCts;
    private int _disposed;
    private bool _started;

    public string Id => BackendId;
    public string DisplayName => "Linux evdev";
    public bool SupportsPressRelease => true;
    public bool IsGlobalScope => true;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    public event EventHandler? PromptPaletteRequested;
    public event EventHandler? TransformSelectionRequested;
    public event EventHandler? RecentTranscriptionsRequested;
    public event EventHandler? CopyLastTranscriptionRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler<string>? Failed;

    public EvdevGlobalShortcutBackend()
    {
        _dispatcher.DictationToggleRequested += () => DictationToggleRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStartRequested += () => DictationStartRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStopRequested += () => DictationStopRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.PromptPaletteRequested += () => PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.TransformSelectionRequested += () => TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.RecentTranscriptionsRequested += () => RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CopyLastTranscriptionRequested += () => CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.CancelRequested += () => CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAvailable()
    {
        // We treat the backend as available when at least one keyboard
        // device exists and is readable by the current user. The cheapest
        // truthful probe is to open one device read-only and close it.
        var devices = KeyboardDeviceDiscovery.EnumerateKeyboards();
        if (devices.Count == 0) return false;

        foreach (var d in devices)
        {
            try
            {
                using var s = new FileStream(d, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return true;
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
        }
        return false;
    }

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        _dispatcher.UpdateShortcuts(shortcuts);

        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return Task.FromResult(new GlobalShortcutRegistrationResult(
                    Success: false, BackendId: BackendId,
                    UserMessage: "evdev backend is disposed.",
                    RequiresToggleMode: false, TroubleshootingCommand: null));
            }

            if (!_started)
            {
                AttachAllDevices_NoLock();
                StartHotPlugWatcher_NoLock();
                StartPeriodicRescan_NoLock();
                _started = true;

                if (_readers.Count == 0)
                {
                    return Task.FromResult(new GlobalShortcutRegistrationResult(
                        Success: false, BackendId: BackendId,
                        UserMessage: "No accessible keyboards under /dev/input. Add your user to the 'input' group.",
                        RequiresToggleMode: false,
                        TroubleshootingCommand: "sudo usermod -aG input $USER"));
                }
            }
        }

        return Task.FromResult(new GlobalShortcutRegistrationResult(
            Success: true, BackendId: BackendId,
            UserMessage: null,
            RequiresToggleMode: false, TroubleshootingCommand: null));
    }

    public Task UnregisterAsync(CancellationToken ct)
    {
        _dispatcher.ClearShortcuts();
        return Task.CompletedTask;
    }

    private void AttachAllDevices_NoLock()
    {
        foreach (var path in KeyboardDeviceDiscovery.EnumerateKeyboards())
            TryAttach_NoLock(path);
    }

    private void TryAttach_NoLock(string path)
    {
        if (_readers.ContainsKey(path)) return;
        var reader = new EvdevDeviceReader(path, OnKeyEvent, OnReaderFailure);
        if (reader.TryStart())
        {
            _readers[path] = reader;
            Trace.WriteLine($"[EvdevBackend] Attached {path}");
        }
        else
        {
            _ = reader.DisposeAsync();
        }
    }

    private void StartHotPlugWatcher_NoLock()
    {
        if (_watcher is not null) return;
        if (!Directory.Exists(InputDir)) return;

        try
        {
            _watcher = new FileSystemWatcher(InputDir, "event*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
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
        if (_rescanCts is not null) return;
        _rescanCts = new CancellationTokenSource();
        var ct = _rescanCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(RescanInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                Rescan();
            }
        });
    }

    private void OnDeviceCreated(object? sender, FileSystemEventArgs e)
    {
        // Kernel creates /dev/input/eventN before its by-path symlink
        // resolves. Retry the discovery a few times with backoff to ride
        // through the race.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                if (Rescan()) return;
            }
        });
    }

    private void OnDeviceDeleted(object? sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            if (_readers.Remove(e.FullPath, out var reader))
                _ = reader.DisposeAsync();
        }
    }

    private bool Rescan()
    {
        var added = false;
        List<EvdevDeviceReader>? toDispose = null;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1) return false;

            // Prune readers whose backing path is gone — protects against
            // the FileSystemWatcher dropping a Delete event under load.
            foreach (var existing in _readers.Keys.ToList())
            {
                if (!File.Exists(existing))
                {
                    if (_readers.Remove(existing, out var stale))
                    {
                        toDispose ??= new();
                        toDispose.Add(stale);
                    }
                }
            }

            foreach (var path in KeyboardDeviceDiscovery.EnumerateKeyboards())
            {
                if (_readers.ContainsKey(path)) continue;
                TryAttach_NoLock(path);
                added = true;
            }
        }

        if (toDispose is not null)
        {
            foreach (var r in toDispose)
                _ = r.DisposeAsync();
        }
        return added;
    }

    private void OnKeyEvent(string devicePath, int linuxKeyCode, bool pressed)
    {
        // Modifier transition: update our aggregated mask atomically so two
        // keyboards mashing modifiers at the same instant don't lose bits.
        var modBit = LinuxKeyMap.ToModifier(linuxKeyCode);
        if (modBit != ModifierMask.None)
        {
            var bitsInt = (int)modBit;
            if (pressed) Interlocked.Or(ref _liveModifiersBits, bitsInt);
            else Interlocked.And(ref _liveModifiersBits, ~bitsInt);
            // Modifiers can still be bindable in their own right (the user
            // might bind RightCtrl as the dictation key). Fall through to
            // the dispatcher so press/release on a modifier reaches the
            // shortcut matcher too.
        }

        var sharpHookKey = LinuxKeyMap.ToSharpHook(linuxKeyCode);
        if (sharpHookKey is null) return;

        var mods = (ModifierMask)Volatile.Read(ref _liveModifiersBits);
        // When the *trigger* key is itself a modifier, the bit for that key
        // will be set in `mods` on press. Mask it out so the chord matches
        // a "no other modifiers" binding like `RightCtrl`.
        if (modBit != ModifierMask.None) mods &= ~modBit;

        _dispatcher.Handle(sharpHookKey.Value, mods, pressed);
    }

    private void OnReaderFailure(string path, Exception ex)
    {
        Trace.WriteLine($"[EvdevBackend] Reader {path} failed: {ex.Message}");
        lock (_lock)
        {
            if (_readers.Remove(path, out var reader))
                _ = reader.DisposeAsync();
        }
        // Clear the aggregated modifier mask: a held modifier on the lost
        // device would otherwise stay "down" forever. We err toward
        // releasing modifiers on disconnect — the next press from any
        // remaining keyboard re-asserts what's actually held.
        Volatile.Write(ref _liveModifiersBits, 0);
        Failed?.Invoke(this, $"Lost keyboard device {path}: {ex.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        FileSystemWatcher? watcher;
        CancellationTokenSource? rescan;
        List<EvdevDeviceReader> readers;
        lock (_lock)
        {
            watcher = _watcher;
            _watcher = null;
            rescan = _rescanCts;
            _rescanCts = null;
            readers = _readers.Values.ToList();
            _readers.Clear();
        }

        try { watcher?.Dispose(); }
        catch (Exception ex) { Trace.WriteLine($"[EvdevBackend] Watcher dispose threw: {ex.Message}"); }

        if (rescan is not null)
        {
            try { rescan.Cancel(); rescan.Dispose(); }
            catch (Exception ex) { Trace.WriteLine($"[EvdevBackend] Rescan dispose threw: {ex.Message}"); }
        }

        foreach (var r in readers)
        {
            try { await r.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Trace.WriteLine($"[EvdevBackend] Reader dispose threw: {ex.Message}"); }
        }
    }
}
