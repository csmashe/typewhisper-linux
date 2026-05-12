# Phase 2 — Evdev Backend (fixes the bug)

Status: blocked on Phase 1
Depends on: Phase 1 (interface extraction)
Unblocks: Phase 3 (portal as alternate path)

## Goal

Add an `EvdevGlobalShortcutBackend` that reads `/dev/input/event*` directly. Make it the primary Wayland backend so `Ctrl+Shift+Space` (or the user's configured chord) fires globally — including when a text editor, browser, or terminal is focused. Preserve Toggle / PushToTalk / Hybrid recording modes.

**This phase fixes the reported bug.**

## Scope

In scope:

- `EvdevDeviceReader` — opens one `/dev/input/event*` device, reads 24-byte `input_event` records asynchronously, emits `(linuxKeyCode, pressed)` events.
- `LinuxKeyMap` — translates kernel `KEY_*` codes to SharpHook `KeyCode` enum values so the Phase-1 `ShortcutMatcher` is reused.
- `EvdevGlobalShortcutBackend` — discovers keyboards, owns N device readers, tracks live modifier mask, drives `ShortcutMatcher`, raises `IGlobalShortcutBackend` events.
- Hotplug: `FileSystemWatcher` on `/dev/input` attaches new keyboards as they appear.
- `BackendSelector` updated: Wayland → try evdev first; if `IsAvailable()` returns false, fall through to SharpHook (current degraded behavior) until Phase 3 lands.
- Welcome / setup banner: detect Wayland + check `input` group membership + show `sudo usermod -aG input $USER` instruction with a copy button when not in the group.
- Default evdev-mode hotkey: `RightCtrl`. SharpHook-mode default stays `Ctrl+Shift+Space`.
- Idempotency hardening in the in-process state machine (see "Idempotency" below).

Out of scope:

- Portal backend (Phase 3).
- Any CLI or IPC (Phase 4+).
- DE-specific helpers (Phase 6).
- uinput device grabbing to consume the keystroke (explicitly rejected — see Risks).

## Files

### New

```
src/TypeWhisper.Linux/Services/Hotkey/Evdev/EvdevGlobalShortcutBackend.cs
src/TypeWhisper.Linux/Services/Hotkey/Evdev/EvdevDeviceReader.cs
src/TypeWhisper.Linux/Services/Hotkey/Evdev/LinuxKeyMap.cs
src/TypeWhisper.Linux/Services/Hotkey/Evdev/InputEventStruct.cs
src/TypeWhisper.Linux/Services/Hotkey/Evdev/KeyboardDeviceDiscovery.cs
```

### Modified

```
src/TypeWhisper.Linux/Services/Hotkey/BackendSelector.cs       # add evdev path
src/TypeWhisper.Linux/ServiceRegistrations.cs                  # register evdev backend
src/TypeWhisper.Linux/App.axaml.cs                             # welcome banner if missing input group
src/TypeWhisper.Linux/Services/DictationOrchestrator.cs        # idempotency assertions (see below)
```

## Implementation notes

### The input_event struct

From `linux/input.h`, 24 bytes on 64-bit Linux:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public long TimeSec;    // time_t — 64-bit on glibc x86_64/aarch64
    public long TimeUsec;   // suseconds_t
    public ushort Type;     // EV_KEY = 1, EV_SYN = 0
    public ushort Code;     // KEY_LEFTCTRL = 29, KEY_SPACE = 57, ...
    public int Value;       // 0 = release, 1 = press, 2 = repeat
}
```

Add a startup assertion: `Debug.Assert(Marshal.SizeOf<InputEvent>() == 24);` and a hard runtime check that fails the backend init with a clear error if violated.

### Device discovery

Primary: enumerate `/dev/input/by-path/*-event-kbd`. Resolve each symlink to its `/dev/input/eventN` target.

Fallback (rare): if `by-path` is missing, walk `/dev/input/event*` and probe each with `ioctl(EVIOCGBIT(EV_KEY))` to check whether it has any of `KEY_A..KEY_Z`. If yes, treat as keyboard.

`IsAvailable()` returns true iff at least one discovered device can be opened for read.

### Reading loop

Each device gets its own `EvdevDeviceReader` with a dedicated `Task` running:

```csharp
var buf = new byte[24];
while (!ct.IsCancellationRequested)
{
    int read = 0;
    while (read < 24)
    {
        var n = await _stream.ReadAsync(buf.AsMemory(read, 24 - read), ct);
        if (n == 0) return;            // device unplugged
        read += n;
    }
    var evt = MemoryMarshal.Read<InputEvent>(buf);
    if (evt.Type != 1) continue;       // only EV_KEY
    if (evt.Value == 2) continue;      // ignore repeats
    KeyEvent?.Invoke(evt.Code, evt.Value == 1);
}
```

Open with `FileShare.ReadWrite` — multiple readers are allowed by the kernel; we don't claim exclusive access.

### Modifier state

Evdev gives individual key transitions, not a modifier mask. Maintain our own:

```csharp
private ModifierMask _live = ModifierMask.None;

void HandleKey(int linuxCode, bool pressed)
{
    var bit = LinuxKeyMap.ToModifier(linuxCode);   // None for non-modifier keys
    if (bit != ModifierMask.None)
    {
        if (pressed) _live |= bit; else _live &= ~bit;
        return;
    }
    var sharpHookKey = LinuxKeyMap.ToSharpHook(linuxCode);
    if (sharpHookKey is null) return;
    var match = ShortcutMatcher.Match(sharpHookKey.Value, _live, pressed, _shortcuts);
    if (match is not null) Dispatch(match);
}
```

Aggregate the mask across all keyboard devices — modifiers might come from one keyboard and the trigger from another.

### Hotplug

```csharp
_watcher = new FileSystemWatcher("/dev/input") { Filter = "event*" };
_watcher.Created += (_, e) => Task.Run(() => TryAttachReader(e.FullPath));
_watcher.EnableRaisingEvents = true;
```

Add a small debounce — the kernel sometimes creates `/dev/input/eventN` before its by-path symlink resolves, so retry the symlink probe with a 250ms backoff up to 3 times.

### Default hotkey under evdev

Evdev sees the keystroke but does **not** consume it. So `Ctrl+Shift+Space` under evdev would still type a space into the focused doc. Two-track defaults:

| Backend | Default | Reason |
|---|---|---|
| SharpHook (X11) | `Ctrl+Shift+Space` | Unchanged. Today's behavior. |
| Evdev (Wayland with input group) | `RightCtrl` | Single key, not in normal typing flow. Matches whisper-overlay's default. |
| Portal / compositor (future) | `Ctrl+Shift+Space` | Compositor consumes the chord. |

The welcome banner should surface this when the active backend is evdev:

> Push-to-talk works globally. To avoid typing a space into your document, we set the dictation key to **Right Ctrl**. You can change it in Settings → Shortcuts.

### Idempotency

ch-recommendation flagged this and it's worth doing in this phase rather than waiting for the CLI:

- `DictationOrchestrator.StartDictationAsync` while already recording → return immediately, no-op.
- `DictationOrchestrator.StopDictationAsync` while idle → return immediately, no-op.
- `DictationOrchestrator.ToggleDictationAsync` reads current state and dispatches to start or stop.

Why now: PushToTalk + Hybrid modes generate paired press/release events. If the user releases the key after a crash, or the modifier-key release arrives before the trigger-key release, we want robust no-ops rather than stuck "transcribing" states. Add an explicit semaphore around state transitions if one isn't already present.

## DI changes

```csharp
services.AddSingleton<EvdevGlobalShortcutBackend>();
// BackendSelector.Resolve():
//   x11    -> SharpHook
//   wayland: try evdev.IsAvailable() else SharpHook (Phase 3 will chain portal between)
```

## Welcome / setup UX

Banner conditions:

- `XDG_SESSION_TYPE == "wayland"` AND user is not in `input` group.
- Show inside the app, top of main window or in Settings → Shortcuts.

Banner content:

> **Global hotkeys need keyboard input access.** Add your user to the `input` group so TypeWhisper can detect your dictation key while other apps are focused.
>
> ```
> sudo usermod -aG input $USER
> ```
>
> Then log out and back in. Until then, hotkeys only work while TypeWhisper is the active window.

Check group membership: read `/proc/self/status` for `Groups:` line and compare against `getgrnam("input")->gr_gid`, or shell out to `groups | grep -q input`. The first is cleaner.

## Privacy and security messaging

When the evdev backend activates for the first time, log a clear single-line statement:

```
[Hotkey] evdev backend active — reading keyboard events to detect your configured shortcut. No keystroke content is logged.
```

In settings, surface:

> TypeWhisper reads keyboard events from the kernel to detect your dictation shortcut. It only acts on the configured key combination — it does not log, record, or transmit the content of your keystrokes.

## Exit criteria

### X11 (regression check)

- TypeWhisper focused: shortcut works.
- Text editor focused: shortcut works.
- Push-to-talk: press starts, release stops.
- (Same as today — no regression.)

### Wayland with evdev permission

- TypeWhisper focused: shortcut works.
- Gedit / VS Code / Firefox / Terminal / LibreOffice Writer focused: shortcut works.
- Push-to-talk: press starts, release stops, transcription runs.
- Hybrid: behaves per user's configured tap/hold thresholds, regardless of focused window.
- Hot-plug a USB keyboard mid-session: new keyboard's events are detected on the next press.

### Wayland without evdev permission

- App does not pretend global PTT works.
- Banner is visible explaining how to enable it.
- Toggle still works while TypeWhisper is focused (degraded SharpHook behavior — same as today).

### Manual stress tests

| Test | Pass condition |
|---|---|
| 50 rapid press/release cycles with no speech | No stuck "recording" state. State returns to idle. |
| Hold key, switch focused window 5 times, release | Release event fires; recording stops. |
| Crash the app while key is held (`kill -9`); restart | Next key press works normally. No leftover state. |
| Two keyboards connected; press modifier on one, trigger on the other | Chord fires. |
| Non-US layout (e.g., German QWERTZ) | Configured chord by kernel `KEY_*` code still works (layout-independent). |
| Bluetooth keyboard disconnect during session | No crash; reader cleans up. Reconnect → reader reattaches. |

## What this phase explicitly does NOT do

- Does NOT add a portal backend. Users without `input` group still see today's broken behavior (Phase 3 fixes that with toggle-only portal).
- Does NOT add CLI commands. No `typewhisper record start`. Phase 4/5.
- Does NOT add per-desktop shortcut helpers (gsettings, KGlobalAccel, hyprctl). Phase 6.
- Does NOT use uinput to grab/consume the keystroke. Documented anti-pattern — adds kernel-grab dependency and breaks if another app grabs the same device. Reconsider only if users complain about typed-through keys after this phase ships.

## Risks

| Risk | Mitigation |
|---|---|
| Chord-not-consumed: `Ctrl+Shift+Space` types a space into the focused app under evdev. | Default evdev to `RightCtrl`. Banner explains the change. User can override with any chord, but UI warns when chord includes printable keys. |
| User refuses `input` group. | Falls through to SharpHook for now (focused-window only). Phase 3 will add portal toggle fallback. |
| User's distro doesn't have an `input` group. | `usermod -aG` will error. Detect this case and fall back to suggesting a udev rule. Document but don't auto-install (sudo prompt is too invasive). |
| `time_t` width on 32-bit-time builds. | Runtime assertion at startup. We don't ship 32-bit-time targets. |
| Flatpak / snap sandboxes don't expose `/dev/input`. | `IsAvailable()` returns false. Falls through correctly. |
| Reading `/dev/input` looks like a keylogger. | Single-line log statement on activation, settings page explanation, only the configured chord triggers actions. |
| FileSystemWatcher misses a hot-plug event under load. | Add a periodic (30-second) re-scan as backup. Cheap; just checks symlinks. |
