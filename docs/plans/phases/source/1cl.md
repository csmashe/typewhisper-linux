# Wayland Push-to-Talk Hotkey: Field Research and Implementation Plan

Date: 2026-05-12
Author: Claude (research + recommendation, no code merged yet)
Status: Proposal — companion to `2026-05-12-wayland-global-shortcuts-design.md`. Where the two disagree, this doc reflects later research into 19 comparable projects and should win.

## TL;DR

- TypeWhisper's current `HotkeyService` uses **SharpHook/libuiohook**, which under Wayland is effectively X11-only and silently degrades to focused-window-only events. That is the literal cause of "Ctrl+Shift+Space fires when my app is foreground but does nothing when a text doc is foreground."
- Across 19 open-source Wayland speech-to-text apps surveyed, **three patterns** actually work; one pattern (the one we ship) does not. See the comparison table below.
- **Recommendation: add an `evdev` backend as the Wayland default.** It is the only path that preserves TypeWhisper's existing `PushToTalk` and `Hybrid` recording modes, works on every compositor (Hyprland, KDE, GNOME, Sway, river), and is what the most successful Wayland dictation projects converged on (whisper-overlay, hyprwhspr, stt2desktop, WhisperTux, Wayland-Voice-Typer).
- The existing design doc (`2026-05-12-wayland-global-shortcuts-design.md`) puts the **XDG GlobalShortcuts portal first**. That is fine for users who don't want to join the `input` group, but most portal implementations do not deliver key-release events, so the portal degrades PTT/Hybrid to toggle. Keep the portal — make it the secondary backend, not the primary.
- Suggested merge order: evdev backend first (fixes the reported bug, preserves PTT). Portal + GNOME/KDE/Hyprland fallbacks second (broader rollout, no `input` group required, toggle-only).

## Why this document exists

The user reported that on Fedora GNOME Wayland, the default `Ctrl+Shift+Space` dictation hotkey only fires while the TypeWhisper window is focused. Asked to survey "how do other apps solve this?". I investigated 19 Linux/Wayland speech-to-text projects, categorized their approaches, and matched the findings against TypeWhisper's current `HotkeyService` and the existing design doc.

## Background: why Wayland breaks our hotkey

Wayland's security model deliberately blocks non-focused clients from observing keystrokes destined for other clients. There is no equivalent to X11's `XGrabKey`. The compositor owns all input and only routes it to the focused surface.

Three escape hatches exist:

1. **evdev** — open `/dev/input/event*` directly and read the kernel's raw input stream. Bypasses the display server entirely. Needs membership in the `input` group (or a udev rule). Works under any compositor, any session, even on the login greeter.
2. **`xdg-desktop-portal-GlobalShortcuts`** — a D-Bus portal at `org.freedesktop.portal.GlobalShortcuts` on `/org/freedesktop/portal/desktop`. Implemented by `xdg-desktop-portal-kde`, `xdg-desktop-portal-gnome` (Mutter-backed), and `xdg-desktop-portal-hyprland`. **Not implemented by `xdg-desktop-portal-wlr`** as of 2026 (issue #240 still open), so Sway / river / generic wlroots have no portal support.
3. **Compositor binds** — let Sway / Hyprland / KWin run a shell command on a keybind, which talks back to the running app via D-Bus, Unix socket, or signals.

What does **not** work on Wayland (the bucket we are currently in):

- X11 keyboard hooks via `XGrabKey` or `XRecord` — SharpHook/libuiohook, Tauri's `tauri-plugin-global-shortcut`, Electron `globalShortcut`, Python `pynput`. All of them only see keystrokes while the app window is focused under Wayland.

## Field research: 19 projects surveyed

The four parallel research agents read READMEs and source for each repo. Full per-project notes are in the conversation transcript; what follows is the synthesized comparison.

### Pattern A — evdev (the dominant Wayland-first choice)

| Project | Repo | Hotkey source file | PTT support | Permissions |
|---|---|---|---|---|
| whisper-overlay | `oddlama/whisper-overlay` | `src/keyboard.rs`, `src/hotkeys.rs` | Yes | `input` group |
| hyprwhspr (default) | `goodroot/hyprwhspr` | Python evdev, configurable | Yes | `input` group |
| stt2desktop | `jedie/stt2desktop` | `stt2desktop/stt.py:96-149` | Yes | `input` group + uinput udev rule (for ydotool) |
| WhisperTux | `cjams/whispertux` | `src/global_shortcuts.py` | Yes | `input`, `tty` groups |
| Wayland-Voice-Typer | `danielrosehill/Wayland-Voice-Typer` | `app/src/`, `requirements.txt` declares `evdev>=1.6.0` | Yes | `input` group |
| OpenWhispr | `OpenWhispr/openwhispr` | bundled `resources/bin/linux-key-listener-*` binary | Yes | input access (binary emits `NO_PERMISSION`) |
| Voxtype (default) | `peteonrails/voxtype` | Rust evdev | Yes (PTT in evdev mode only) | `input` group |

Quote from whisper-overlay's README that captures the consensus:
> "The global hotkey is detected using evdev, since I didn't manage to get the GlobalShortcuts desktop portal to work with windows using the layer-shell protocol."

### Pattern B — XDG GlobalShortcuts portal

| Project | Repo | Notes |
|---|---|---|
| Speed of Sound | `zugaldia/speedofsound` | Uses sister project `zugaldia/stargate` (Kotlin wrapper over `org.freedesktop.portal.*`). Only project surveyed that uses the portal as its primary path. |

### Pattern C — Compositor bind + IPC/CLI/signal

| Project | Repo | Mechanism |
|---|---|---|
| Handy | `cjpais/Handy` | `handy --toggle-transcription` CLI, or `pkill -USR2 -n handy` signal. README explicitly tells the user to bind it in their compositor. |
| Hyprvoice | `LeonardoTrapani/hyprvoice` | Hyprland-only. `bind = SUPER, R, exec, hyprvoice toggle` + `bindr = ...` for release. Talks to daemon over Unix socket at `~/.cache/hyprvoice/control.sock`. |
| hyprwhspr (opt-in) | `goodroot/hyprwhspr` | `bindd = SUPER ALT, D, ..., exec, hyprwhspr-tray.sh record` |
| waystt | `sevos/waystt` | SIGUSR1 to the daemon. `bind = SUPER, R, exec, pkill --signal SIGUSR1 waystt` |
| whispers | `onenoted/whispers` | `bind = SUPER ALT, D, exec, whispers`. Toggle semantics — press to start, press to transcribe. |
| BlahST | `QuantiusBenignus/BlahST` | DE shortcut UI runs `wsi -p`. Same instructions for X11 and Wayland. |
| nerd-dictation | `ideasman42/nerd-dictation` | `nerd-dictation begin / end / cancel`. Zero in-app hotkey logic; delegated entirely to user. |
| Voxtype (recommended) | `peteonrails/voxtype` | Docs recommend compositor binds over evdev. `voxtype record start|stop|toggle`. |

### Pattern D — IBus engine (different problem)

| Project | Repo | Notes |
|---|---|---|
| IBus-Speech-To-Text | `PhilippeRo/IBus-Speech-To-Text` | Not a global-hotkey app at all. Registered as an IBus input method; activated via the IBus IME-switch (`Win+Space`). Only works inside IBus-aware text fields, which excludes most Electron/Chromium apps and some games. Not a useful model for TypeWhisper. |

### Anti-patterns (broken on Wayland — this is where we currently live)

| Project | Repo | What goes wrong |
|---|---|---|
| Whispering / Epicenter | `EpicenterHQ/epicenter` | `tauri-plugin-global-shortcut`, which is X11-only. README has no Wayland section. Same symptom as our bug. |
| MySuperWhisper ("linux-whisper" search match) | `OlivierMary/MySuperWhisper` | `pynput.keyboard.Listener`. Source even has comments about "X11 state" corruption. README claims "X11 or Wayland" but the code is X11-bound. |
| **TypeWhisper (us, today)** | this repo | `SharpHook/libuiohook` in `src/TypeWhisper.Linux/Services/HotkeyService.cs`. Hook starts but only sees keys while our window has focus. |

### Projects that did not resolve to a Wayland dictation app

- "VoxCtl" — two GitHub hits, both unrelated. `majjoha/voxctl` is a macOS music CLI; `ln64-git/voxctl` is a TTS tool. Neither does Wayland dictation.

## Comparison: PTT viability per pattern

This is the deciding factor for TypeWhisper because `HotkeyService.cs` ships three modes (`Toggle`, `PushToTalk`, `Hybrid` — see `src/TypeWhisper.Linux/Services/HotkeyService.cs:52` and the press/release branches at lines 432-444 and 510-523).

| Pattern | Press event | Release event | Toggle | PushToTalk | Hybrid |
|---|---|---|---|---|---|
| evdev | yes | yes | works | **works** | **works** |
| XDG portal (KDE Plasma 6) | yes | not delivered | works | broken (no release) | degrades to toggle |
| XDG portal (GNOME 45+) | yes | not delivered | works | broken | degrades to toggle |
| Compositor bind, generic | yes (via `exec`) | no | works | broken | degrades to toggle |
| Compositor bind, Hyprland `bindr` | yes | yes | works | works | works |
| SharpHook on X11 | yes | yes | works | works | works |
| SharpHook on Wayland | only when focused | only when focused | **broken** (your bug) | broken | broken |

If we want PTT/Hybrid to keep working on Wayland — and TypeWhisper's overlay UX is built around hold-to-talk — evdev is the only universal answer.

## Recommendation

### Backend priority (Wayland)

This replaces the order in `2026-05-12-wayland-global-shortcuts-design.md` § "Backend Priority":

1. **X11 session** → SharpHook (unchanged from today).
2. **Wayland, user in `input` group** → **evdev backend (new — primary Wayland path)**. Supports Toggle/PushToTalk/Hybrid. Universal across compositors. No D-Bus, no portal dependency.
3. **Wayland, no `input` group, portal available** → XDG GlobalShortcuts portal. Toggle-only. UI must surface "PTT/Hybrid unavailable on this backend — add user to `input` group to enable."
4. **Wayland, no portal, GNOME** → `gsettings` custom-keybindings + our own D-Bus callback service (already in the existing design doc, keep as-is).
5. **Wayland, no portal, KDE** → KGlobalAccel via D-Bus. Toggle-only.
6. **Wayland, no portal, Hyprland** → `hyprctl keyword bind` (press) + `bindr` (release). Hyprland is the only compositor that gives us release through this path, so PTT/Hybrid become available again.
7. **Anything else** → show copyable `gdbus call` snippet for manual binding.

### First-run wizard ask

Mirror the onboarding pattern from hyprwhspr / stt2desktop:

> "TypeWhisper works best when it can read keystrokes globally (so push-to-talk works even when another app is focused).
>
> This requires adding your user to the `input` group. Run:
> `sudo usermod -aG input $USER` and log out / log back in.
>
> Or, skip this step — TypeWhisper will fall back to your desktop's global-shortcut system, but push-to-talk and hybrid modes will be disabled."

### Default Wayland hotkey

`Ctrl+Shift+Space` is fine on the portal/compositor-bind paths because the compositor consumes the chord before the focused app sees it. **But evdev does not consume the keystroke** (whisper-overlay's README is explicit on this). If we ship evdev with `Ctrl+Shift+Space` as the default, holding the chord will also insert a space into the focused text doc.

Two reasonable mitigations:

- **A. Change the default for evdev mode** to a chord-free key — `F13` (whisper-overlay default for "macro" keys), `RightCtrl` (whisper-overlay's actual default), or `Pause`. Surface this in the welcome wizard.
- **B. Use uinput to grab the device** so the chord is swallowed. Complex; none of the surveyed projects bothered. Don't do this in v1.

Default `RightCtrl` for evdev mode (matches whisper-overlay's default), keep `Ctrl+Shift+Space` for portal/compositor modes.

## Architecture: how this lands in TypeWhisper

The interface from `2026-05-12-wayland-global-shortcuts-design.md` already fits. Reusing it:

```csharp
public interface IGlobalShortcutBackend : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsPressRelease { get; }
    bool IsAvailable();
    Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct);
    Task UnregisterAsync(CancellationToken ct);

    event EventHandler? DictationToggleRequested;
    event EventHandler? DictationStartRequested;
    event EventHandler? DictationStopRequested;
    event EventHandler? PromptPaletteRequested;
    event EventHandler? TransformSelectionRequested;
    event EventHandler? RecentTranscriptionsRequested;
    event EventHandler? CopyLastTranscriptionRequested;
    event EventHandler? CancelRequested;
}
```

Two new backends to add (the SharpHook one is implicit — wrap the existing `HotkeyService` body):

- `EvdevGlobalShortcutBackend` (this doc — new, primary Wayland path)
- `SharpHookGlobalShortcutBackend` (wrapper around the existing logic — X11 only)

The portal and compositor-specific backends come later (already designed in the companion doc).

### Backend selection

```csharp
public static IGlobalShortcutBackend SelectBackend(IServiceProvider services)
{
    var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

    if (session == "x11")
        return services.GetRequiredService<SharpHookGlobalShortcutBackend>();

    // Wayland (or unknown — assume Wayland on Linux post-2025).
    var evdev = services.GetRequiredService<EvdevGlobalShortcutBackend>();
    if (evdev.IsAvailable())
        return evdev;

    var portal = services.GetRequiredService<XdgPortalGlobalShortcutsBackend>();
    if (portal.IsAvailable())
        return portal;

    // GNOME / KDE / Hyprland fallbacks chained in the order from the design doc.
    return services.GetRequiredService<ManualCommandBackend>();
}
```

`EvdevGlobalShortcutBackend.IsAvailable()` returns true iff at least one keyboard device under `/dev/input/event*` is readable by the current process.

## EvdevGlobalShortcutBackend — implementation sketch

Two viable .NET implementations: P/Invoke `libevdev.so.2`, or read `/dev/input/event*` directly with `FileStream` + struct marshaling. The latter is what I recommend — no native dependency, ~200 lines, the kernel ABI is rock-stable.

### The Linux input event struct

From `linux/input.h` — 24 bytes on 64-bit Linux:

```c
struct input_event {
    struct timeval time;   // 16 bytes: time_t tv_sec + suseconds_t tv_usec
    __u16 type;            // 2 bytes:  EV_KEY = 1, EV_SYN = 0, ...
    __u16 code;            // 2 bytes:  KEY_LEFTCTRL = 29, KEY_SPACE = 57, ...
    __s32 value;           // 4 bytes:  0 = release, 1 = press, 2 = repeat
};
```

C# equivalent:

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public long TimeSec;     // time_t — 64-bit on glibc Linux x86_64/arm64
    public long TimeUsec;    // suseconds_t — same width
    public ushort Type;
    public ushort Code;
    public int Value;
}
// sizeof(InputEvent) == 24 on x86_64 / aarch64 Linux
```

### Keyboard device discovery

`/dev/input/by-path/*-event-kbd` is the canonical "this is a keyboard" symlink set on udev systems. Fall back to ioctl `EVIOCGBIT(EV_KEY)` against every `/dev/input/event*` if the by-path tree is missing (rare).

```csharp
private static IEnumerable<string> FindKeyboardDevices()
{
    const string byPath = "/dev/input/by-path";
    if (Directory.Exists(byPath))
    {
        foreach (var link in Directory.EnumerateFiles(byPath, "*-event-kbd"))
        {
            // Resolve the symlink so we open /dev/input/eventN directly —
            // by-path symlinks are stable, but the underlying event device
            // is what we read.
            var resolved = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
                yield return resolved.FullName;
        }
    }
}
```

USB / Bluetooth keyboards plugged in *after* startup will not be picked up by this single scan — for that, watch `/dev/input` via `FileSystemWatcher` and re-scan on `Created`.

### Reading events

Open each device as a `FileStream` with `FileAccess.Read | FileShare.ReadWrite` (the kernel allows multiple readers — we are not exclusive). Read 24 bytes at a time. Decode.

```csharp
internal sealed class EvdevDeviceReader : IDisposable
{
    private readonly string _path;
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event Action<int /*code*/, bool /*pressed*/>? KeyEvent;

    public EvdevDeviceReader(string devicePath)
    {
        _path = devicePath;
        // Buffered with the exact event size so each ReadAsync hands us one event.
        _stream = new FileStream(devicePath, FileMode.Open, FileAccess.Read,
                                 FileShare.ReadWrite, bufferSize: 24,
                                 useAsync: true);
    }

    public void Start() => _loop = Task.Run(() => ReadLoopAsync(_cts.Token));

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[24];
        while (!ct.IsCancellationRequested)
        {
            int read = 0;
            while (read < 24)
            {
                var n = await _stream.ReadAsync(buf.AsMemory(read, 24 - read), ct);
                if (n == 0) return; // device unplugged
                read += n;
            }

            var evt = MemoryMarshal.Read<InputEvent>(buf);
            if (evt.Type != 1 /* EV_KEY */) continue;

            // value 0 = release, 1 = press, 2 = repeat — we ignore repeats here
            // and let HotkeyService's own _keyIsDown guard handle dedup.
            if (evt.Value == 2) continue;

            KeyEvent?.Invoke(evt.Code, evt.Value == 1);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stream.Dispose();
        _cts.Dispose();
    }
}
```

### Keycode mapping

Linux keycodes are defined in `/usr/include/linux/input-event-codes.h`. The values we need overlap with SharpHook's `KeyCode` enum but the integer values differ — SharpHook uses libuiohook's `VC_*` virtual codes, Linux uses kernel `KEY_*` codes. We need a translation table. The relevant subset (modifiers + the keys our shortcut parser already accepts):

```csharp
internal static class LinuxKeyMap
{
    // linux/input-event-codes.h
    public const int KEY_LEFTCTRL  = 29;
    public const int KEY_RIGHTCTRL = 97;
    public const int KEY_LEFTSHIFT = 42;
    public const int KEY_RIGHTSHIFT = 54;
    public const int KEY_LEFTALT   = 56;
    public const int KEY_RIGHTALT  = 100;  // AltGr on most layouts
    public const int KEY_LEFTMETA  = 125;
    public const int KEY_RIGHTMETA = 126;

    public const int KEY_SPACE   = 57;
    public const int KEY_ENTER   = 28;
    public const int KEY_TAB     = 15;
    public const int KEY_ESC     = 1;
    // F1..F12 = 59..68; F13..F24 = 183..194
    // A..Z   = 30..50-ish (not contiguous — use a lookup table)
    // 1..0   = 2..11

    public static KeyCode? ToSharpHook(int linuxCode) => linuxCode switch
    {
        KEY_SPACE => KeyCode.VcSpace,
        KEY_ENTER => KeyCode.VcEnter,
        KEY_TAB   => KeyCode.VcTab,
        KEY_ESC   => KeyCode.VcEscape,
        59 => KeyCode.VcF1,
        // ... fill in the rest from input-event-codes.h
        _ => null,
    };
}
```

We translate to SharpHook's `KeyCode` so we don't have to touch `HotkeyService.cs`'s existing matching logic. The backend produces `KeyCode` + `ModifierMask` and feeds them into the same `OnKeyPressed` / `OnKeyReleased` shapes the current code already understands.

### Modifier state tracking

Unlike libuiohook, evdev does not give us a modifier mask per event — only the individual key transitions. We maintain our own mask:

```csharp
private ModifierMask _liveMask = ModifierMask.None;

private void HandleKey(int linuxCode, bool pressed)
{
    var bit = linuxCode switch
    {
        LinuxKeyMap.KEY_LEFTCTRL  => ModifierMask.LeftCtrl,
        LinuxKeyMap.KEY_RIGHTCTRL => ModifierMask.RightCtrl,
        LinuxKeyMap.KEY_LEFTSHIFT => ModifierMask.LeftShift,
        LinuxKeyMap.KEY_RIGHTSHIFT => ModifierMask.RightShift,
        LinuxKeyMap.KEY_LEFTALT   => ModifierMask.LeftAlt,
        LinuxKeyMap.KEY_RIGHTALT  => ModifierMask.RightAlt,
        LinuxKeyMap.KEY_LEFTMETA  => ModifierMask.LeftMeta,
        LinuxKeyMap.KEY_RIGHTMETA => ModifierMask.RightMeta,
        _ => ModifierMask.None,
    };

    if (bit != ModifierMask.None)
    {
        if (pressed) _liveMask |= bit;
        else _liveMask &= ~bit;
        return; // modifiers don't themselves trigger shortcuts
    }

    var sharpHookKey = LinuxKeyMap.ToSharpHook(linuxCode);
    if (sharpHookKey is null) return;

    if (pressed) RaiseKeyPressed(sharpHookKey.Value, _liveMask);
    else         RaiseKeyReleased(sharpHookKey.Value, _liveMask);
}
```

### Backend skeleton

```csharp
public sealed class EvdevGlobalShortcutBackend : IGlobalShortcutBackend
{
    private readonly List<EvdevDeviceReader> _readers = new();
    private FileSystemWatcher? _hotplugWatcher;
    private ModifierMask _liveMask = ModifierMask.None;

    public string Id => "linux-evdev";
    public string DisplayName => "Linux evdev (works on Wayland and X11)";
    public bool SupportsPressRelease => true;

    public event EventHandler? DictationToggleRequested;
    public event EventHandler? DictationStartRequested;
    public event EventHandler? DictationStopRequested;
    // ...other events from IGlobalShortcutBackend

    public bool IsAvailable()
    {
        foreach (var dev in FindKeyboardDevices())
        {
            try
            {
                using var s = File.Open(dev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return true; // we can open at least one keyboard
            }
            catch (UnauthorizedAccessException) { /* try next */ }
            catch (IOException)                  { /* try next */ }
        }
        return false;
    }

    public Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        // Parse the user's hotkey strings ("Ctrl+Shift+Space", etc.) into
        // (KeyCode, ModifierMask) tuples — HotkeyService.TryParseHotkey
        // already does this; lift it into a shared helper.
        ParseShortcutsInto(shortcuts);

        foreach (var dev in FindKeyboardDevices())
        {
            try
            {
                var reader = new EvdevDeviceReader(dev);
                reader.KeyEvent += (code, pressed) => HandleKey(code, pressed);
                reader.Start();
                _readers.Add(reader);
            }
            catch (UnauthorizedAccessException)
            {
                return Task.FromResult(new GlobalShortcutRegistrationResult(
                    Success: false,
                    BackendId: Id,
                    UserMessage: "Cannot read /dev/input — add your user to the 'input' group.",
                    RequiresToggleMode: false,
                    TroubleshootingCommand: "sudo usermod -aG input $USER && reboot"));
            }
        }

        WatchForHotplugs();

        return Task.FromResult(new GlobalShortcutRegistrationResult(
            Success: _readers.Count > 0,
            BackendId: Id,
            UserMessage: $"Reading {_readers.Count} keyboard device(s) directly.",
            RequiresToggleMode: false));
    }

    private void WatchForHotplugs()
    {
        _hotplugWatcher = new FileSystemWatcher("/dev/input") { Filter = "event*" };
        _hotplugWatcher.Created += (_, e) => TryAttachReader(e.FullPath);
        _hotplugWatcher.EnableRaisingEvents = true;
    }

    // ...HandleKey, RaiseKeyPressed, RaiseKeyReleased dispatching to the events above.

    public async ValueTask DisposeAsync()
    {
        _hotplugWatcher?.Dispose();
        foreach (var r in _readers) r.Dispose();
        _readers.Clear();
        await Task.CompletedTask;
    }
}
```

### Where this plugs into existing code

`src/TypeWhisper.Linux/Services/HotkeyService.cs:37` currently instantiates `TaskPoolGlobalHook` (SharpHook) directly. After refactor:

- Extract the existing `OnKeyPressed` / `OnKeyReleased` matching logic into a pure helper that operates on `(KeyCode, ModifierMask, bool pressed)` tuples — independent of where the events come from.
- Make `HotkeyService` consume `IGlobalShortcutBackend` events and dispatch through that helper.
- `ServiceRegistrations.cs:69` (`services.AddSingleton<HotkeyService>()`) stays. Add `services.AddSingleton<EvdevGlobalShortcutBackend>()` and `services.AddSingleton<SharpHookGlobalShortcutBackend>()` and a `SelectBackend(...)` resolver.
- `App.axaml.cs:90,177` already calls `hotkey.Initialize()` — that's the entry point where backend selection happens.

The orchestrator wiring at `DictationOrchestrator.cs:25` is unchanged; it still subscribes to the same `DictationStartRequested` / `DictationStopRequested` / `DictationToggleRequested` events.

## Testing plan

### Unit tests (no real devices)

- `LinuxKeyMap.ToSharpHook` round-trips for every entry.
- Modifier state machine: press LeftCtrl → mask is `LeftCtrl`; press LeftCtrl + RightCtrl + release LeftCtrl → mask is `RightCtrl`; spurious release of un-pressed key → mask unchanged.
- Synthetic event injection: feed a stream of `InputEvent` bytes through a `MemoryStream`-backed reader, assert that the right events fire on the backend.

### Integration tests (real device, manual)

On Fedora GNOME Wayland (the user's environment, where the bug reproduces):

1. `echo $XDG_SESSION_TYPE` → should print `wayland`.
2. `ls -la /dev/input/by-path/*-event-kbd` → confirm at least one keyboard symlink.
3. `groups` → must include `input`. If not: `sudo usermod -aG input $USER && reboot`.
4. Start TypeWhisper with the new backend.
5. Open a text editor (gedit, VS Code), focus it.
6. Press `Ctrl+Shift+Space` (or whatever the new default is in evdev mode) — **dictation overlay should appear**.
7. Hold the chord for 1 second, release — Hybrid mode should stop on release. This is the smoking-gun test.
8. Press Escape during recording — `CancelRequested` should fire.

Repeat the matrix on:
- KDE Plasma 6 Wayland
- Hyprland
- Sway (canonical wlroots compositor — confirms evdev works where the portal doesn't)
- X11 (sanity check that the SharpHook path still works)

## Risks and caveats

| Risk | Mitigation |
|---|---|
| Keystroke is not consumed — `Ctrl+Shift+Space` will type a space into the focused app. | Change evdev default to `RightCtrl` (whisper-overlay's default) or `F13`. Document in the wizard. |
| Reading `/dev/input` is privacy-sensitive — we see every keystroke on the system. | We already use SharpHook which has the same property on X11. Document it in the welcome wizard. Process never logs raw keystrokes; only fires our own events. |
| User refuses the `input` group. | Falls through to portal/compositor backends, with PTT disabled and a UI note. Toggle still works. |
| USB / Bluetooth keyboard hot-plug. | `FileSystemWatcher` on `/dev/input` for `event*` creations. Already in the sketch above. |
| Wrong endianness or struct size on 32-bit ARM. | We don't ship for 32-bit. Add a runtime assertion `Marshal.SizeOf<InputEvent>() == 24` at startup; bail with a clear error otherwise. |
| `time_t` width on musl / 32-bit-time builds. | `glibc` Linux on x86_64 / aarch64 is 64-bit. We don't support 32-bit-time platforms; assert at startup. |
| Concurrent access — what if another app also reads `/dev/input/event0`? | evdev devices allow multiple readers. The kernel duplicates events to all openers. No conflict. |
| What about Flatpak / snap sandboxes? | They typically don't expose `/dev/input`. `IsAvailable()` returns false there, falls through to portal. This is actually correct behavior. |

## What I am NOT recommending

- **Don't ship uinput keystroke grabbing.** It would consume the chord but adds a kernel-grab dependency, breaks if another app grabs the same device first, and none of the surveyed projects do it. Re-evaluate only if users complain about typed-through keys.
- **Don't drop SharpHook.** It is still the right answer on X11. Wrap it behind the same `IGlobalShortcutBackend` interface and keep it for `XDG_SESSION_TYPE=x11`.
- **Don't make the portal the default Wayland path.** It cannot deliver release events on KDE/GNOME, so PTT/Hybrid silently degrade to toggle. Keep it as a secondary backend for users who refuse the `input` group.
- **Don't write a Hyprland-specific path before evdev lands.** Hyprvoice is great if you only need Hyprland, but TypeWhisper targets the whole Linux desktop. evdev covers Hyprland for free.

## Suggested commit order

1. **PR 1 — refactor**: extract `IGlobalShortcutBackend` interface, wrap the existing SharpHook logic as `SharpHookGlobalShortcutBackend`. No behavior change. Tests stay green.
2. **PR 2 — evdev backend**: `EvdevGlobalShortcutBackend`, `LinuxKeyMap`, `EvdevDeviceReader`. Wire into backend selection. Welcome wizard ask. Switch evdev default key. Manual QA on Fedora GNOME Wayland + Hyprland + Sway.
3. **PR 3 — portal backend**: `XdgPortalGlobalShortcutsBackend` per the companion design doc. Toggle-only path for users without `input` group.
4. **PR 4 — DE-specific fallbacks**: GNOME `gsettings`, KDE KGlobalAccel, Hyprland `hyprctl`. Manual command fallback UI. Each is independently small once the interface is in place.
5. **PR 5 — Shortcuts settings UI**: surface active backend, supported modes, troubleshooting command, fallback `gdbus call` snippet.

## Reference: full list of projects surveyed

OpenWhispr · Whispering/Epicenter · Whisper-Wayland (whisper-overlay) · VoxCtl (not found) · Handy · Hyprvoice · hyprwhspr · waystt · BlahST · stt2desktop · Speed of Sound · Voxtype · MySuperWhisper (linux-whisper match) · nerd-dictation (Dictate match) · WhisperTux · Wayland Voice Typer · IBus Speech To Text · whispers · plus the global-hotkey libraries each one uses.

GitHub URLs are in the parent conversation's research output; this doc deliberately doesn't reproduce the long quotes — read it alongside that transcript or re-run the same searches if you need to verify a specific claim.
