# Phase 2 — Implementation Plan

## Approach

Add an evdev-based `IGlobalShortcutBackend` that reads `/dev/input/event*` directly so the user's configured chord fires globally on Wayland. Make it the primary Wayland backend; fall through to SharpHook when evdev is unavailable.

To keep press/release/Mode semantics identical to the SharpHook backend, extract the press/release state machine into a shared `ShortcutDispatcher`; both backends own one and call into it from their respective event sources.

## New files

1. `src/TypeWhisper.Linux/Services/Hotkey/ShortcutDispatcher.cs` — shared (KeyCode, ModifierMask, pressed) state machine. Holds repeat-guards, `_dictationKeyDownTime`, and the Mode-aware press/release dispatch from today's SharpHook code. Exposes simple delegate events that backends subscribe to and re-raise as `IGlobalShortcutBackend` events.
2. `src/TypeWhisper.Linux/Services/Hotkey/Evdev/InputEventStruct.cs` — 24-byte `input_event` struct + runtime size assertion.
3. `src/TypeWhisper.Linux/Services/Hotkey/Evdev/LinuxKeyMap.cs` — `int → KeyCode?` and `int → ModifierMask` lookups for the chord keys the existing parser supports (letters, digits, F1–F24, Space/Enter/Tab/Escape/Backspace/Delete/Home/End/PgUp/PgDn/arrows, plus modifiers Ctrl/Shift/Alt/Meta and `RightCtrl` for the evdev default).
4. `src/TypeWhisper.Linux/Services/Hotkey/Evdev/KeyboardDeviceDiscovery.cs` — enumerate `/dev/input/by-path/*-event-kbd`; fallback walk of `/dev/input/event*` is out of scope for v1 (cited in phase doc as "rare" — we log a warning and report unavailable instead, to avoid relying on `ioctl(EVIOCGBIT)` p/invoke in this phase).
5. `src/TypeWhisper.Linux/Services/Hotkey/Evdev/EvdevDeviceReader.cs` — opens one device, runs a 24-byte read loop on a dedicated Task, emits `(linuxCode, pressed)` events. Ignores `EV_KEY` repeats (`Value == 2`) and non-`EV_KEY` events.
6. `src/TypeWhisper.Linux/Services/Hotkey/Evdev/EvdevGlobalShortcutBackend.cs` — owns N readers + a single `ShortcutDispatcher`; maintains an aggregated modifier mask across all keyboards; hot-plug via `FileSystemWatcher` on `/dev/input` with a 30 s periodic re-scan as backup.
7. `src/TypeWhisper.Linux/Services/InputGroupCheck.cs` — utility that returns "is the current user in the `input` group?" by parsing `/proc/self/status` `Groups:` line and `/etc/group`.

## Modified files

1. `src/TypeWhisper.Linux/Services/Hotkey/SharpHookGlobalShortcutBackend.cs` — replace the inline press/release handlers with a `ShortcutDispatcher`.
2. `src/TypeWhisper.Linux/Services/Hotkey/BackendSelector.cs` — pick evdev first on Wayland (`XDG_SESSION_TYPE == "wayland"`) when `evdev.IsAvailable()`; otherwise SharpHook.
3. `src/TypeWhisper.Linux/ServiceRegistrations.cs` — register the new evdev backend.
4. `src/TypeWhisper.Linux/Services/DictationOrchestrator.cs` — idempotency assertions: Start while recording → no-op; Stop while idle → no-op; Toggle dispatches based on current state. Add a semaphore around state transitions if one isn't already there.
5. `src/TypeWhisper.Linux/Views/MainWindow.axaml(.cs)` or `App.axaml.cs` — add a Wayland + missing-input-group banner. Surfacing the recommended `sudo usermod -aG input $USER` command, copy-to-clipboard button.

## Out of scope (deferred to later phases or follow-ups)

- Portal backend — Phase 3.
- CLI / IPC — Phase 4+.
- DE helpers — Phase 6.
- uinput grabbing — explicitly rejected per phase doc.
- Two-track default-hotkey switch (`RightCtrl` under evdev): the configured default lives in `AppSettings` and is read at startup; switching the **persisted** default by backend would require migration logic. Plan: keep `Ctrl+Shift+Space` as the persisted default, but expose a one-time recommendation in the banner — "Evdev backend active: consider using a non-printing key like Right Ctrl to avoid typing a space." The recommendation is informational, not enforced.

## Tests

- `LinuxKeyMapTests` — round-trip key code mappings for the chord keys.
- `ShortcutDispatcherTests` — verify press/release/repeat-guard/Mode semantics so the refactor of SharpHookBackend is provably equivalent. Cover all three Modes plus the Hybrid threshold.
- `InputGroupCheckTests` — parses synthetic `/proc/self/status` content.
- The existing 154 Linux tests must still pass.

## Risks/mitigations

- **Refactor must preserve behavior.** Add `ShortcutDispatcherTests` *before* swapping `SharpHookBackend` over to the dispatcher; run the full test suite after the swap to catch any regression.
- **`input` group missing in container/sandbox.** `IsAvailable()` returns false → SharpHook fallback. The banner only surfaces on Wayland sessions where the user could plausibly fix it.
- **Hot-plug debounce.** Retry symlink resolution with a small backoff (250 ms × 3) before giving up on a newly-created `eventN`.
- **Thread safety.** Dispatcher's state is touched only from the reader threads (each device has its own task). Use simple locks around the repeat-guards + key-down-time so two keyboards pressing the same key at the same instant doesn't corrupt state.

## Exit criteria

- `dotnet build` clean.
- Full `dotnet test tests/TypeWhisper.Linux.Tests` passes.
- `BackendSelector` picks evdev on Wayland (when available); SharpHook on X11 or when evdev is unavailable.
- A unit test asserts the dispatcher fires the right event for each Mode under press/release sequences.
