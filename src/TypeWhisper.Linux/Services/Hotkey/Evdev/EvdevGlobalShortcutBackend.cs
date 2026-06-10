using SharpHook.Native;
using System.Diagnostics;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Reads keyboard input directly from <c>/dev/input/event*</c> for global
///     hotkey detection on Wayland (SharpHook only sees events while focused).
///     Requires the user to be in the <c>input</c> group; <see cref="IsAvailable" />
///     returns false when no device can be opened so callers fall through to SharpHook.
/// </summary>
public sealed class EvdevGlobalShortcutBackend : IGlobalShortcutBackend
{
    public const string BackendId = "linux-evdev";
    private const string InputDir = "/dev/input";

    // Belt-and-suspenders rescan: FileSystemWatcher can miss events under high I/O load.
    // 30 s catches a late-plugged USB keyboard without hammering the kernel.
    private static readonly TimeSpan s_rescanInterval = TimeSpan.FromSeconds(30);

    private readonly ShortcutDispatcher _dispatcher = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, EvdevDeviceReader> _readers = new();
    private int _disposed;

    // Aggregated modifier state across all keyboards. evdev gives individual key
    // transitions; we maintain the mask via Interlocked.Or/And so concurrent
    // reader tasks can update bits without a read-modify-write race.
    private int _liveModifiersBits;
    private CancellationTokenSource? _rescanCts;
    private bool _started;

    private FileSystemWatcher? _watcher;

    public EvdevGlobalShortcutBackend()
    {
        _dispatcher.DictationToggleRequested += () =>
            DictationToggleRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStartRequested += () =>
            DictationStartRequested?.Invoke(this, EventArgs.Empty);
        _dispatcher.DictationStopRequested += () =>
            DictationStopRequested?.Invoke(this, EventArgs.Empty);
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
        // Available when at least one keyboard device is readable. Cheapest probe: open+close.
        var devices = KeyboardDeviceDiscovery.EnumerateKeyboards();
        if (devices.Count == 0)
        {
            return false;
        }

        foreach (var d in devices)
        {
            try
            {
                using var s = new FileStream(
                    d,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );
                return true;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return false;
    }

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct
    )
    {
        _dispatcher.UpdateShortcuts(shortcuts);

        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return Task.FromResult(
                    new GlobalShortcutRegistrationResult(
                        false,
                        BackendId,
                        "evdev backend is disposed.",
                        false,
                        null
                    )
                );
            }

            if (!_started)
            {
                AttachAllDevices_NoLock();
                StartHotPlugWatcher_NoLock();
                StartPeriodicRescan_NoLock();
                _started = true;

                if (_readers.Count == 0)
                {
                    return Task.FromResult(
                        new GlobalShortcutRegistrationResult(
                            false,
                            BackendId,
                            "No accessible keyboards under /dev/input. Add your user to the 'input' group.",
                            false,
                            "sudo usermod -aG input $USER"
                        )
                    );
                }
            }
        }

        return Task.FromResult(
            new GlobalShortcutRegistrationResult(
                true,
                BackendId,
                null,
                false,
                null
            )
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
                rescan.Cancel();
                rescan.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevBackend] Rescan dispose threw: {ex.Message}");
            }
        }

        foreach (var r in readers)
        {
            try
            {
                await r.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevBackend] Reader dispose threw: {ex.Message}");
            }
        }
    }

    private void AttachAllDevices_NoLock()
    {
        foreach (var path in KeyboardDeviceDiscovery.EnumerateKeyboards())
        {
            TryAttach_NoLock(path);
        }
    }

    private void TryAttach_NoLock(string path)
    {
        if (_readers.ContainsKey(path))
        {
            return;
        }

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
        if (_watcher is not null)
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
        if (_rescanCts is not null)
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
        });
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

    private void OnDeviceDeleted(object? sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            if (_readers.Remove(e.FullPath, out var reader))
            {
                _ = reader.DisposeAsync();
            }
        }
    }

    private bool Rescan()
    {
        var added = false;
        List<EvdevDeviceReader>? toDispose = null;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return false;
            }

            // Prune readers for paths that vanished — guards against FSW dropping Delete events under load.
            foreach (var existing in _readers.Keys.ToList())
            {
                if (!File.Exists(existing))
                {
                    if (_readers.Remove(existing, out var stale))
                    {
                        toDispose ??= new List<EvdevDeviceReader>();
                        toDispose.Add(stale);
                    }
                }
            }

            foreach (var path in KeyboardDeviceDiscovery.EnumerateKeyboards())
            {
                if (_readers.ContainsKey(path))
                {
                    continue;
                }

                TryAttach_NoLock(path);
                added = true;
            }
        }

        if (toDispose is not null)
        {
            foreach (var r in toDispose)
            {
                _ = r.DisposeAsync();
            }
        }

        return added;
    }

    private void OnKeyEvent(string devicePath, int linuxKeyCode, bool pressed)
    {
        // Update the aggregated modifier mask atomically so concurrent keyboards don't lose bits.
        var modBit = LinuxKeyMap.ToModifier(linuxKeyCode);
        if (modBit != ModifierMask.None)
        {
            var bitsInt = (int)modBit;
            if (pressed)
            {
                Interlocked.Or(ref _liveModifiersBits, bitsInt);
            }
            else
            {
                Interlocked.And(ref _liveModifiersBits, ~bitsInt);
            }
            // Modifiers can themselves be the trigger key (e.g. RightCtrl bound to dictation),
            // so fall through to the dispatcher.
        }

        var sharpHookKey = LinuxKeyMap.ToSharpHook(linuxKeyCode);
        if (sharpHookKey is null)
        {
            return;
        }

        var mods = (ModifierMask)Volatile.Read(ref _liveModifiersBits);
        // If the trigger key is itself a modifier, its bit will be set in mods on press.
        // Mask it out so a "no other modifiers" binding like RightCtrl still matches.
        if (modBit != ModifierMask.None)
        {
            mods &= ~modBit;
        }

        _dispatcher.Handle(sharpHookKey.Value, mods, pressed);
    }

    private void OnReaderFailure(string path, Exception ex)
    {
        Trace.WriteLine($"[EvdevBackend] Reader {path} failed: {ex.Message}");
        lock (_lock)
        {
            if (_readers.Remove(path, out var reader))
            {
                _ = reader.DisposeAsync();
            }
        }

        // Clear modifier mask on disconnect: a held modifier on the lost device would
        // stay "down" forever otherwise. The next press from any remaining keyboard re-asserts.
        Volatile.Write(ref _liveModifiersBits, 0);
        Failed?.Invoke(this, $"Lost keyboard device {path}: {ex.Message}");
    }
}