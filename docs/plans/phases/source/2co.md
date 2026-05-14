# Wayland Push-To-Talk Research And Recommendation

Date: 2026-05-12

## Context

TypeWhisper currently uses `SharpHook` / `libuiohook` for Linux global hotkeys in `src/TypeWhisper.Linux/Services/HotkeyService.cs`.

That works reliably on X11, but it is not a reliable global keyboard capture mechanism on Wayland. The observed behavior matches that limitation:

- When TypeWhisper is focused, `Ctrl+Shift+Space` starts dictation.
- When another Wayland-native app is focused, the same shortcut does nothing.

The issue is not the dictation pipeline itself. It is the global hotkey capture layer.

## Projects Reviewed

I looked at representative Linux / Wayland speech-to-text projects and grouped their approaches by hotkey strategy.

### Evdev-Based Hotkey Capture

This is the most common approach among Wayland-first dictation tools that support push-to-talk.

Projects:

- Voxtype: https://github.com/peteonrails/voxtype
- hyprwhspr: https://github.com/goodroot/hyprwhspr
- WhisperTux: https://github.com/cjams/whispertux
- stt2desktop: https://github.com/jedie/stt2desktop
- linux-whisper: https://kwap.github.io/linux-whisper/

What they do:

- Read keyboard events directly from `/dev/input/event*`.
- Detect keyboard devices by capabilities.
- Track pressed keys and modifier state.
- Emit both press and release events.
- Use the release event for push-to-talk stop behavior.
- Require elevated input permissions, usually by adding the user to the `input` group or installing udev rules.

Notable examples:

- Voxtype has a Rust `evdev_listener.rs` that opens keyboard devices, tracks modifiers, ignores repeats, handles press/release events, and watches `/dev/input` with `inotify` for hotplug.
- hyprwhspr uses Python `evdev`, supports selected keyboard devices, hotplug handling, and optional device grabbing with `UInput` passthrough.
- WhisperTux uses Python `evdev` to track a target key set and fire both press and release callbacks.
- stt2desktop documents an evdev hotkey such as `KEY_SCROLLLOCK` and uses release to transcribe and paste.

Strengths:

- Works across GNOME, KDE, Hyprland, Sway, Niri, and other Wayland compositors.
- Supports true push-to-talk because key release is visible.
- Does not depend on each compositor's shortcut APIs.
- Fits TypeWhisper's current event model: `DictationStartRequested`, `DictationStopRequested`, and `DictationToggleRequested`.

Weaknesses:

- Requires access to `/dev/input/event*`.
- This is sensitive from a security standpoint because input devices can reveal all keyboard activity.
- Needs careful setup UX and clear permission explanation.
- Needs robust device filtering to avoid mice, media remotes, and virtual keyboards.

### Compositor Or Desktop Shortcut Integration

Projects:

- OpenWhispr: https://github.com/OpenWhispr/openwhispr
- waystt: https://github.com/sevos/waystt
- BlahST: https://github.com/QuantiusBenignus/BlahST

What they do:

- Register shortcuts with the desktop or compositor, or ask the user to create compositor keybindings.
- GNOME can use custom keyboard shortcuts via `gsettings`.
- KDE can use KGlobalAccel over D-Bus.
- Hyprland can use `hyprctl keyword bind`.
- Minimal tools can expose CLI commands and let the window manager run them.

OpenWhispr is a good reference here:

- GNOME path: creates custom GNOME keybindings that call back into the app over D-Bus.
- KDE path: uses KGlobalAccel D-Bus.
- Hyprland path: registers `hyprctl` binds that call the app over D-Bus.
- Fallback path: Electron `globalShortcut`.

Strengths:

- Aligns with Wayland's security model.
- No raw keyboard device access.
- Users can inspect and edit shortcuts in desktop settings.
- Good for toggle-style dictation.

Weaknesses:

- Not portable across compositors without per-desktop code.
- Often only gives activation, not reliable key release.
- Poor fit for true push-to-talk unless the compositor supports separate press and release bindings.
- GNOME custom shortcuts and many command-based bindings are toggle-friendly, not hold-friendly.

### XDG Desktop Portal GlobalShortcuts

Reference:

- XDG GlobalShortcuts portal: https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html

What it provides:

- A standard portal for global shortcuts.
- `Activated` and `Deactivated` signals.
- User-mediated binding flow.
- Better security story than evdev.

Strengths:

- The right long-term API for desktop-integrated global shortcuts.
- It can theoretically support push-to-talk because it has deactivation signals.
- Better for sandboxed and permission-aware environments.

Weaknesses:

- Support varies by desktop and portal backend.
- More moving parts: xdg-desktop-portal, backend implementation, app id, session handling, and user binding UI.
- Some current apps still avoid relying on it as their only path because behavior differs across GNOME, KDE, Hyprland, and wlroots setups.

### IBus Speech-To-Text

Projects / references:

- Fedora ibus-speech-to-text change: https://fedoraproject.org/wiki/Changes/ibus-speech-to-text
- PhilippeRo IBus-Speech-To-Text: https://github.com/PhilippeRo/IBus-Speech-To-Text

What it does:

- Implements speech-to-text as an IBus input method.
- Text goes through the input method system instead of simulated keyboard input.
- Users switch to the speech input method, similar to language/input switching.

Strengths:

- Good Wayland security model.
- Integrates with text input instead of faking keys.
- Potentially works wherever IBus works.

Weaknesses:

- Different product model from TypeWhisper.
- Requires the user to switch input methods.
- Less suitable for TypeWhisper's current app-wide shortcut, profiles, prompt actions, overlays, and post-processing workflow.

## Text Insertion Notes

TypeWhisper already has a better Wayland story for output than for capture.

Current TypeWhisper output behavior already prefers:

- `wl-copy` / `wl-paste` for Wayland clipboard.
- `wtype` for Wayland paste/key events.
- `xdotool` for X11 and some XWayland fallback cases.

Other tools generally do similar things:

- Voxtype uses clipboard plus `wtype`, `eitype`, or `ydotool`.
- hyprwhspr uses clipboard plus `wtype` first, then `ydotool`.
- stt2desktop uses `wl-copy` plus `ydotool key ctrl+v`.
- WhisperTux uses `ydotool`.

The main gap in TypeWhisper is hotkey capture, not text insertion.

## Recommendation

Implement a backend-based Linux hotkey system:

1. Use the existing SharpHook backend on X11.
2. Add an evdev backend for Wayland.
3. Keep the public `HotkeyService` events unchanged so the dictation orchestrator does not need a major rewrite.
4. Add a setup/status surface that explains missing Wayland hotkey permissions.
5. Add optional compositor/CLI fallback later.
6. Add XDG GlobalShortcuts portal support later as a lower-privilege option where it works reliably.

The best immediate fix is evdev, because it is the only approach in the reviewed tools that consistently supports true global push-to-talk on Wayland across desktops.

## Proposed TypeWhisper Design

Create a small backend abstraction:

```csharp
internal interface ILinuxHotkeyBackend : IDisposable
{
    event EventHandler<LinuxHotkeyEvent>? HotkeyEvent;
    event EventHandler<string>? Failed;
    void Start(HotkeyBindings bindings);
    void UpdateBindings(HotkeyBindings bindings);
    void Stop();
}
```

Backends:

- `SharpHookHotkeyBackend`: current implementation, used on X11 and as fallback.
- `EvdevHotkeyBackend`: new Wayland implementation.
- Optional future `PortalHotkeyBackend`: XDG Desktop Portal GlobalShortcuts.
- Optional future `CliShortcutBackend`: no listener, just accepts app commands from compositor bindings.

The existing `HotkeyService` should become the coordinator:

- Detect session type.
- Choose backend.
- Keep parsing and formatting hotkeys.
- Preserve existing events:
  - `DictationToggleRequested`
  - `DictationStartRequested`
  - `DictationStopRequested`
  - `PromptPaletteRequested`
  - `RecentTranscriptionsRequested`
  - `CopyLastTranscriptionRequested`
  - `TransformSelectionRequested`
  - `CancelRequested`

## Evdev Backend Details

The evdev backend should:

- Enumerate `/dev/input/event*`.
- Open only devices with keyboard capabilities.
- Ignore virtual devices created by this app or tools like ydotool when identifiable.
- Track modifier state by key code.
- Track target key press and release.
- Ignore repeat events.
- Fire release even if modifiers are released before the main key, matching current TypeWhisper behavior.
- Re-enumerate devices on hotplug, suspend/resume, or periodic validation.
- Surface clear errors for permission denied and no keyboard found.

For the first implementation, do not grab devices. Passive reading is less invasive and avoids needing to re-emit non-shortcut keys through `uinput`.

Device grabbing can be added later only if the app must suppress the shortcut from reaching the foreground application.

## Permissions UX

Wayland evdev capture will need explicit user setup. The app should detect permission failures and show a clear message such as:

```text
Wayland global shortcuts need keyboard input-device access.

Add your user to the input group, then log out and back in:
sudo usermod -aG input $USER

For stricter setups, install a udev rule for TypeWhisper instead of broad input-group access.
```

Longer term, prefer a dedicated udev group/rule over broadly recommending `input` group membership.

## Hotkey Choice

`Ctrl+Shift+Space` is not ideal as a default Linux Wayland shortcut.

Reasons:

- It is commonly used by editors, terminals, browser apps, and input methods.
- It may be reserved or intercepted before TypeWhisper sees it.
- It is not a dedicated push-to-talk style key.

Better Linux defaults:

- `ScrollLock`
- `Pause`
- `F8`
- `F9`
- `F12`
- `Super+Alt+D`

For push-to-talk specifically, single unused keys such as `ScrollLock`, `Pause`, right `Ctrl`, or right `Alt` tend to work better than multi-key text-editor shortcuts.

## Ranking Of Options

1. **Evdev backend**: best immediate solution for reliable Wayland push-to-talk.
2. **Compositor shortcut commands**: good fallback for toggle mode and users who do not want input-device permissions.
3. **XDG GlobalShortcuts portal**: best long-term standards-based path, but should not be the only backend yet.
4. **IBus input method**: interesting separate product direction, not a direct fit for TypeWhisper's current workflow.
5. **SharpHook/libuiohook on Wayland**: not recommended as the Wayland solution.

## Implementation Plan

1. Extract the current SharpHook code into a `SharpHookHotkeyBackend`.
2. Add a backend-neutral hotkey event model.
3. Add `EvdevHotkeyBackend`.
4. Add session/backend selection:
   - X11: SharpHook.
   - Wayland with evdev access: evdev.
   - Wayland without evdev access: show setup message and optionally keep SharpHook only for focused-window behavior.
5. Add tests for parsing and backend event routing.
6. Add manual test checklist for GNOME Wayland, KDE Wayland, Hyprland, and X11.
7. Later, add `typewhisper --toggle`, `typewhisper --start`, and `typewhisper --stop` commands for compositor shortcut fallback.
8. Later, add XDG GlobalShortcuts portal support.

## Bottom Line

For TypeWhisper's current feature set, especially hybrid mode and push-to-talk, evdev is the practical Wayland fix. It matches the strongest Wayland-first dictation tools and maps cleanly onto the current TypeWhisper hotkey events.

Compositor shortcuts and portals are worth adding as secondary paths, but they should not block the evdev implementation because they do not yet provide the same cross-desktop push-to-talk reliability.
