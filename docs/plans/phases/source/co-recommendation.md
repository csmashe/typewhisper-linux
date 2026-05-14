# Wayland Hotkey Recommendation

Date: 2026-05-12
Status: Recommended direction after reviewing `1cl.md`, `2co.md`, and `3ch.md`

## Summary

Build primarily from `1cl.md`, with the implementation sequencing from `2co.md` and the command/control surface from `3ch.md`.

The immediate bug is that `Ctrl+Shift+Space` only starts dictation when TypeWhisper is focused. That means the current SharpHook/libuiohook listener is not acting as a true global shortcut listener on Wayland. It sees focused-window key events, but it does not reliably see keys while another Wayland app is focused.

The best practical fix is:

1. Keep SharpHook for X11.
2. Add an evdev hotkey backend for Wayland.
3. Add a daemon/CLI control surface so desktop shortcuts and compositors can trigger the same recording actions.
4. Add portal and compositor-specific setup paths after the evdev path works.

## Recommended Architecture

TypeWhisper should have one recording control layer. Every trigger path should call the same actions:

```bash
typewhisper daemon

typewhisper record start
typewhisper record stop
typewhisper record toggle
typewhisper record cancel
typewhisper status
```

Those commands are important, but they are not a replacement for a hotkey backend. They are the control surface. Something still has to invoke them.

Examples:

- Evdev detects the global key press/release and calls the same start/stop actions internally.
- Hyprland or Sway can bind key press to `typewhisper record start` and key release to `typewhisper record stop`.
- GNOME or KDE custom shortcuts can bind `Ctrl+Shift+Space` to `typewhisper record toggle`.
- XDG GlobalShortcuts portal can call start/stop/toggle where the portal backend supports it.

## Backend Priority

Use this order:

1. **X11 session:** SharpHook backend.
2. **Wayland with readable input devices:** evdev backend.
3. **Wayland with portal support:** XDG GlobalShortcuts portal.
4. **Wayland compositor or desktop shortcuts:** bind to the CLI commands.
5. **Fallback:** focused-window shortcuts only, plus clear setup instructions.

## Why Evdev First

Evdev is the only reviewed option that consistently provides both key press and key release across GNOME, KDE, Hyprland, Sway, and other Wayland environments.

That matters because TypeWhisper supports more than toggle mode:

- Toggle needs a press event.
- Push-to-talk needs press and release.
- Hybrid mode needs press and release.

Portal and desktop shortcut systems are better from a security and desktop-integration standpoint, but they are not yet reliable enough across desktops to be the only Wayland answer for push-to-talk.

## Why CLI/Daemon Still Matters

The CLI/daemon design from `3ch.md` is worth building because it gives TypeWhisper a clean external API.

Recommended behavior:

- `record start` should be idempotent. If already recording, it should no-op successfully.
- `record stop` should be idempotent. If already idle, it should no-op successfully.
- `record toggle` should be available for GNOME/KDE custom shortcut fallback.
- `record cancel` should stop recording or suppress pending output.
- `status` should report the current state for scripts, UI, and diagnostics.

This prevents stuck states when shortcut systems repeat commands, release events arrive late, or the app restarts.

## Hotkey Choice

Do not assume `Ctrl+Shift+Space` is the best default for every backend.

For compositor-owned or portal-owned shortcuts, `Ctrl+Shift+Space` is acceptable because the compositor can consume the shortcut before the focused app receives it.

For evdev, the app passively reads keyboard events and does not consume them. That means `Ctrl+Shift+Space` may also send Space to the focused text field.

Recommended defaults:

- Portal/compositor shortcut: `Ctrl+Shift+Space`
- Evdev push-to-talk: `RightCtrl`, `Pause`, `ScrollLock`, or `F13`

The setup UI should explain this and allow the user to choose.

## Implementation Plan

1. Extract current SharpHook logic into `SharpHookHotkeyBackend`.
2. Add a backend-neutral hotkey interface.
3. Add a shared recording command layer: start, stop, toggle, cancel, status.
4. Add the CLI/IPC surface for those commands.
5. Add `EvdevHotkeyBackend` for Wayland press/release capture.
6. Select backend by session and capability:
   - X11: SharpHook.
   - Wayland with evdev access: evdev.
   - Wayland without evdev access: portal or compositor/desktop shortcut setup.
7. Add setup/status UI for missing input permissions.
8. Add portal support and compositor-specific setup helpers.

## User Setup Messaging

For evdev mode, TypeWhisper needs permission to read keyboard input devices. The app should explain this clearly because the permission is sensitive.

Suggested text:

```text
TypeWhisper can use a system input hotkey so push-to-talk works even when another app is focused.

This requires permission to read keyboard input devices.

To enable it, add your user to the input group, then log out and back in:

sudo usermod -aG input $USER

If you prefer not to grant this permission, TypeWhisper can use your desktop's global shortcut system instead, but push-to-talk may fall back to toggle mode on some desktops.
```

## Final Recommendation

Use `1cl.md` as the main technical direction: evdev first for Wayland because it fixes the actual bug and preserves push-to-talk/hybrid behavior.

Use `2co.md` for the clean phased implementation order.

Use `3ch.md` for the daemon/CLI control surface and setup wizard ideas, but do not make compositor bindings the only first solution. They are excellent where supported, but evdev is the more universal Wayland push-to-talk backend.
