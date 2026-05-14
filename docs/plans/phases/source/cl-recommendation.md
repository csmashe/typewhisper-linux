# Wayland Global Hotkey Fix — Recommended Plan

Date: 2026-05-12
Status: Synthesis of `1cl.md`, `2co.md`, `3ch.md`. This document is the working plan; the source docs are kept for reference.

## The bug

On Fedora GNOME Wayland, `Ctrl+Shift+Space` only starts dictation when the TypeWhisper window is focused. When any other app (text editor, browser, terminal) has focus, the chord does nothing.

Root cause: `src/TypeWhisper.Linux/Services/HotkeyService.cs:37` uses SharpHook / libuiohook. Under Wayland, libuiohook degrades to "focused-window-only" because Wayland's security model prevents non-focused clients from observing global keystrokes. There is no `XGrabKey` equivalent.

## Recommendation

Add an **evdev backend** as the primary Wayland hotkey path, keep SharpHook for X11, and layer portal / compositor / CLI fallbacks behind a common backend interface. Do **not** restructure TypeWhisper into a separate daemon + UI client.

This is `1cl.md`'s plan as the implementation base, with selected pieces from `3ch.md` (setup UX, testing matrix, permissions table) and `2co.md` (default hotkey discussion).

## Why this synthesis

### Take from `1cl.md` (base plan)

- **evdev primary on Wayland.** The only path that preserves Toggle / PushToTalk / Hybrid modes universally. Portal and most compositor binds don't deliver key-release on Wayland, so PTT/Hybrid degrade to toggle on those backends.
- **Concrete C# implementation**: `IGlobalShortcutBackend` interface, `EvdevDeviceReader` (~200 LOC, no native deps — reads the 24-byte `input_event` struct directly), `LinuxKeyMap` translating Linux `KEY_*` codes to SharpHook's `KeyCode` enum so existing matching logic in `HotkeyService` is untouched.
- **Backend selection by `XDG_SESSION_TYPE`**: X11 → SharpHook (unchanged), Wayland → evdev if `/dev/input/event*` is readable, else fall through to portal/DE fallbacks.
- **Hotplug handling**: `FileSystemWatcher` on `/dev/input` for `event*` creations.
- **Keystroke isn't consumed**: evdev sees the keystroke but doesn't block it from reaching the focused app. Mitigated by changing the evdev-mode default to a single-key chord (`RightCtrl`, `F13`, or `Pause`).
- **References to actual TypeWhisper code paths**: `HotkeyService.cs:37`, `HotkeyService.cs:432-444,510-523` (PTT/Hybrid press-release branches), `App.axaml.cs:90,177`, `DictationOrchestrator.cs:25`, `ServiceRegistrations.cs:69`. The backend plugs in behind the existing events; orchestrator and overlay code don't change.

### Take from `3ch.md` (UX layer + testing)

- **Setup wizard with desktop detection**: branch on `XDG_CURRENT_DESKTOP` + `WAYLAND_DISPLAY` + `XDG_SESSION_TYPE` and show the recommended path per environment.
- **Permissions UX table** (mic, portal, input group, uinput, clipboard) for the welcome flow.
- **Testing matrix**: 50 rapid press/release cycles, hold-while-switching-windows, crash-and-restart with key held, multiple keyboards connected, non-US layouts, large clipboard contents.
- **Text-insertion fallback chain ordering** (portal/libei → wtype → ydotool → clipboard) as a sanity check against current `IInputService` implementation.

### Take from `2co.md`

- **Default hotkey reconsideration**: `Ctrl+Shift+Space` is a poor PTT default — commonly used by editors, terminals, browsers, and input methods. Better defaults: `ScrollLock`, `Pause`, `F8`, `F13`, `RightCtrl`. This applies whenever we run on evdev (which doesn't consume the keystroke). Surface in the welcome wizard with a per-backend recommendation.

### Reject from `3ch.md`

- **Daemon + CLI + Unix socket + D-Bus as the primary architecture.** TypeWhisper is already a long-running Avalonia app with the recorder, overlay, profiles, orchestrator, and tray in-process. Restructuring into a headless daemon plus a separate UI client is a large refactor that gives us very little our current architecture doesn't already provide.
- **Compositor-bind as the primary path.** It only solves the problem on wlroots compositors with release-bind syntax (`bindr` on Hyprland, `bindsym --release` on Sway). On GNOME Wayland (the user's actual environment), custom shortcuts only fire on activation — no release event — so the "primary" path collapses into toggle-only and loses Hybrid mode.

A **minimal** CLI surface comes later (see Phase 4) for users who want compositor-bind fallback. It does not require a full daemon split.

## Phased plan

### Phase 1 — Interface extraction (no behavior change)

Goal: lift the existing SharpHook implementation behind an interface so we can swap backends.

- Add `IGlobalShortcutBackend` in `src/TypeWhisper.Linux/Services/Hotkey/`.
  - Properties: `Id`, `DisplayName`, `SupportsPressRelease`, `IsAvailable()`.
  - Methods: `RegisterAsync(GlobalShortcutSet, CancellationToken)`, `UnregisterAsync(CancellationToken)`, `IAsyncDisposable`.
  - Events match existing `HotkeyService` events: `DictationToggleRequested`, `DictationStartRequested`, `DictationStopRequested`, `PromptPaletteRequested`, `TransformSelectionRequested`, `RecentTranscriptionsRequested`, `CopyLastTranscriptionRequested`, `CancelRequested`.
- Add `SharpHookGlobalShortcutBackend` wrapping existing `HotkeyService` body. No behavior change. All existing tests pass.
- Add a `SelectBackend(IServiceProvider)` resolver that always returns `SharpHookGlobalShortcutBackend` for now.
- Update DI registration at `ServiceRegistrations.cs:69`.

**Exit criteria**: TypeWhisper behaves identically. The backend swap is the only change.

### Phase 2 — Evdev backend (fixes the reported bug)

Goal: Wayland users on Fedora GNOME (and every other compositor) get global hotkeys back, with full PTT/Hybrid support.

- Add `EvdevGlobalShortcutBackend`, `EvdevDeviceReader`, `LinuxKeyMap`.
- Implementation follows `1cl.md` § "EvdevGlobalShortcutBackend — implementation sketch":
  - Discover keyboards via `/dev/input/by-path/*-event-kbd`, fall back to `EVIOCGBIT(EV_KEY)` ioctl probe.
  - Async `ReadAsync` loop, 24 bytes per event, `MemoryMarshal.Read<InputEvent>`.
  - Modifier state machine (we maintain our own mask — evdev doesn't give us one per event).
  - Hotplug via `FileSystemWatcher` on `/dev/input`.
  - Translate kernel `KEY_*` codes to SharpHook `KeyCode` so existing matching code is reused.
- Update `SelectBackend` to prefer evdev on Wayland when `IsAvailable()` returns true.
- Runtime assertion at startup: `Marshal.SizeOf<InputEvent>() == 24`. Bail with a clear error otherwise.
- Welcome wizard step: detect Wayland, check `input` group membership, show the `sudo usermod -aG input $USER` instruction with a copy button if missing.
- Change the evdev-mode default hotkey to `RightCtrl` (matches whisper-overlay's default). Keep `Ctrl+Shift+Space` for SharpHook / portal / compositor modes.

**Exit criteria**: on Fedora GNOME Wayland with the user in the `input` group, `RightCtrl` (or whatever chord they configure) starts dictation regardless of focused window. Hold-and-release works for Hybrid mode.

### Phase 3 — Portal backend (toggle-only fallback)

Goal: users who refuse the `input` group still get global *toggle* dictation on GNOME / KDE / Hyprland.

- Add `XdgPortalGlobalShortcutsBackend` using `org.freedesktop.portal.GlobalShortcuts`.
- `SupportsPressRelease = false` — most backends don't deliver `Deactivated` reliably. Surface in the UI: "Push-to-talk and Hybrid unavailable on this backend — add user to `input` group to enable."
- Add to backend selection chain: evdev → portal → manual.

**Exit criteria**: Toggle mode works for users without `input` group on GNOME / KDE Plasma 6 / Hyprland. PTT/Hybrid users get a clear message pointing them to the evdev path.

### Phase 4 — Single-instance + minimal CLI

Goal: unblock the GNOME/KDE custom-shortcut fallback path. Users can bind `typewhisper` (no args) to a custom shortcut and get toggle behavior without joining the `input` group and without portal availability.

- Unix domain socket at `$XDG_RUNTIME_DIR/typewhisper/control.sock` (fall back to `/tmp/typewhisper-$UID/control.sock` with `0700` if `XDG_RUNTIME_DIR` is unset).
- On TypeWhisper startup: bind the socket. If already bound, this is a second invocation — send `toggle` to the existing instance and exit.
- That's the entire surface for Phase 4: bare `typewhisper` toggles. No subcommands yet.
- This gives us single-instance enforcement for free (which we arguably want anyway — currently launching twice runs two instances).

**Exit criteria**: a user on GNOME Wayland who refuses both the `input` group and the portal can still bind `typewhisper` to a GNOME custom shortcut and get toggle-mode dictation.

### Phase 5 — Full CLI subcommands

Goal: unblock Hyprland / Sway / Niri true-PTT via compositor `bind` + release-bind.

- Add subcommands: `typewhisper record start`, `typewhisper record stop`, `typewhisper record toggle`, `typewhisper record cancel`, `typewhisper status`.
- JSON line protocol over the existing socket.
- All commands idempotent: `start` while recording → no-op OK; `stop` while idle → no-op OK. Prevents stuck states from key-repeat or crash recovery.

Example Hyprland binding once Phase 5 ships:

```ini
bind  = CTRL SHIFT, SPACE, exec, typewhisper record start
bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop
bind  = CTRL SHIFT, ESCAPE, exec, typewhisper record cancel
```

**Exit criteria**: Hyprland and Sway users get full Hybrid mode via compositor binds without needing the `input` group.

### Phase 6 — DE-specific helpers

Goal: smooth setup. Optional polish.

- GNOME: write `org.gnome.settings-daemon.plugins.media-keys.custom-keybindings` entries via gsettings (merge with existing list, don't overwrite).
- KDE: KGlobalAccel D-Bus registration.
- Hyprland: write `hyprctl keyword bind` / `bindr` snippets for the user to paste, or auto-append to `hyprland.conf` with a confirmation dialog.
- Each is independently small once the interface and CLI exist.

## Backend priority at runtime

After all phases:

| Session | Available | Backend |
|---|---|---|
| X11 | always | SharpHook |
| Wayland | `/dev/input/event*` readable | **evdev** (Toggle / PTT / Hybrid all work) |
| Wayland | portal available, no `input` group | XDG GlobalShortcuts portal (Toggle only) |
| Wayland | nothing else | CLI + DE custom shortcut (Toggle only, requires user setup) |

## Default hotkeys

- **SharpHook / portal / compositor-bind modes**: keep `Ctrl+Shift+Space`. Compositor consumes the chord.
- **Evdev mode**: default to `RightCtrl` (or `F13` if available). Evdev does not consume the keystroke, so a chord like `Ctrl+Shift+Space` would type a space into the focused app. Surface this in the welcome wizard.

## What we're NOT doing

- **Not splitting TypeWhisper into separate daemon + UI processes.** The existing in-process architecture stays.
- **Not implementing uinput keystroke grabbing.** Would consume the chord but adds kernel-grab dependency, breaks if another app grabs the same device, and none of the surveyed projects bothered. Reconsider only if users complain about typed-through keys after Phase 2.
- **Not making portal the default Wayland path.** Toggle-only is a feature regression for current PTT/Hybrid users.
- **Not dropping SharpHook.** Still correct on X11.

## Testing matrix (Phase 2 exit)

Manual QA on:

- **Fedora GNOME Wayland** (the reproducing environment). All three recording modes against gedit, VS Code, Firefox, Terminal, LibreOffice Writer.
- **KDE Plasma 6 Wayland**. Same.
- **Hyprland**. Same.
- **Sway** (canonical wlroots — confirms evdev works where the portal doesn't).
- **X11 session**. Sanity check that SharpHook path is unchanged.

Stress cases:

- 50 rapid press / release cycles → no stuck recording state.
- Hold the hotkey while switching focus between windows → release event still fires.
- Hot-plug a USB keyboard during a session → new device gets picked up by the `FileSystemWatcher`.
- Bluetooth keyboard pair / unpair during a session.
- Multiple keyboards connected simultaneously.
- Non-US layouts (the kernel `KEY_*` codes are layout-independent, but worth verifying our chord parsing doesn't assume otherwise).

## Risks

| Risk | Mitigation |
|---|---|
| `Ctrl+Shift+Space` types a space into the focused doc under evdev. | Default evdev mode to `RightCtrl`. Document in the wizard. |
| Reading `/dev/input` is privacy-sensitive — we see every keystroke. | Already true for SharpHook on X11. Document in the welcome wizard. Process never logs raw keystrokes — only fires our own configured-shortcut events. |
| User refuses `input` group. | Falls through to portal (toggle-only) or CLI + DE custom shortcut. UI note explains PTT/Hybrid require evdev. |
| USB / Bluetooth hot-plug after startup. | `FileSystemWatcher` on `/dev/input`. |
| Flatpak / snap sandbox doesn't expose `/dev/input`. | `IsAvailable()` returns false, falls through to portal. This is correct behavior. |
| 32-bit time_t on weird platforms. | Runtime assertion at startup. We don't support 32-bit-time builds. |

## Bottom line

Phase 1–2 alone fix the reported bug for any user willing to join the `input` group on Wayland. That's the smallest possible diff that solves the problem and preserves Hybrid mode. Phases 3–6 broaden the supported configurations without touching the core fix.

Source documents:
- `docs/plans/1cl.md` — primary implementation source (evdev backend, interface, code-level detail)
- `docs/plans/2co.md` — default hotkey choice
- `docs/plans/3ch.md` — setup UX, testing matrix, permissions table
