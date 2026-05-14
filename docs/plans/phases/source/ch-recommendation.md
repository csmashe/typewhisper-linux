# TypeWhisper Wayland Hotkey Recommendation

Date: 2026-05-12

## Summary

Build from document 1 as the main technical direction.

Use document 2 as the short implementation spec and PR roadmap.

Borrow selected architecture ideas from document 3, especially the daemon, CLI commands, idempotent recording state machine, setup wizard, diagnostics, and compositor integration docs.

Do not use document 3's backend priority as the main plan. It puts compositor shortcuts and the XDG GlobalShortcuts portal before evdev. That is safer from a permissions standpoint, but it is not the best match for TypeWhisper if push-to-talk and hybrid mode need to work reliably on Wayland.

The recommended direction is:

```text
X11:
  Keep SharpHook.

Wayland with input-device permission:
  Use a new evdev backend as the primary global hotkey backend.

Wayland without input-device permission:
  Offer portal, desktop shortcut, compositor binding, CLI, or D-Bus fallback.
```

This directly fixes the issue where Ctrl+Shift+Space only works while the TypeWhisper app is focused.

## The actual problem

The current hotkey path appears to rely on SharpHook/libuiohook. That works well enough on X11, but under Wayland it cannot reliably observe keys while another app is focused.

That is why this happens:

```text
TypeWhisper window focused:
  Ctrl+Shift+Space starts dictation.

Text editor focused:
  Ctrl+Shift+Space does nothing.
```

This is not mainly a bug in the key combo. It is a backend problem. The app is currently listening from a place that Wayland does not allow to behave like a true global key listener.

The fix is to add a real Wayland-compatible trigger path.

## Recommended source document

Use document 1 as the primary source.

Document 1 is the strongest because it focuses on the deciding requirement for TypeWhisper:

```text
Does the backend provide both global key press and global key release events?
```

That matters because TypeWhisper supports more than simple toggle mode. Push-to-talk and hybrid mode need release detection.

For example:

```text
Press hotkey:
  Start recording.

Release hotkey:
  Stop recording and transcribe.
```

Many Wayland-friendly shortcut systems can trigger an activation event, but they often do not provide a reliable release event. That makes them fine for toggle mode but weak for true push-to-talk.

## How the three documents should be used

| Document | How to use it | Why |
| --- | --- | --- |
| 1cl.md | Main technical recommendation | Best match for the bug and for push-to-talk/hybrid behavior. |
| 2co.md | Practical implementation spec | Cleaner and easier to turn into PRs. |
| 3ch.md | Architecture reference | Good daemon, CLI, state machine, setup wizard, and compositor integration ideas. |

## Final backend priority

Use this backend priority instead of a portal-first approach.

### 1. X11 session: SharpHook

Keep the current SharpHook path for X11.

```text
XDG_SESSION_TYPE=x11
  -> SharpHookGlobalShortcutBackend
```

SharpHook works in the environment it was designed for, so there is no need to replace it for X11 users.

### 2. Wayland with input access: evdev

Make evdev the main Wayland backend when the user has granted input-device access.

```text
XDG_SESSION_TYPE=wayland
User has input-device access
  -> EvdevGlobalShortcutBackend
```

This is the best universal option for TypeWhisper because it can detect both press and release events outside the focused app.

This supports:

```text
Toggle mode:        yes
Push-to-talk mode:  yes
Hybrid mode:        yes
```

The tradeoff is permissions. Reading from `/dev/input/event*` is sensitive because it can theoretically observe keyboard input. The app should be transparent about this and should only track the configured shortcut state.

### 3. Wayland without input access: XDG GlobalShortcuts portal

Use the XDG GlobalShortcuts portal as a lower-permission fallback where available.

```text
Wayland
No evdev permission
Portal available
  -> PortalGlobalShortcutBackend
```

This is better from a security and desktop-integration standpoint, but it may not reliably preserve push-to-talk behavior across desktops.

Use this mainly for:

```text
Toggle mode
Simple start action
Simple stop action if the portal/backend supports it
```

The UI should clearly show when the active backend does not support release events.

Example message:

```text
The current shortcut backend supports activation but not reliable release detection.
Push-to-talk and hybrid mode require the evdev backend or a compositor binding with release support.
```

### 4. Desktop custom shortcut fallback

For GNOME, KDE, and similar environments, allow a desktop shortcut to call TypeWhisper through CLI or D-Bus.

Example commands:

```bash
typewhisper record toggle
typewhisper record start
typewhisper record stop
```

This is reliable for toggle mode. It is usually not ideal for push-to-talk unless the desktop can bind a separate release action.

### 5. Compositor bindings

Support compositor-owned shortcuts for users on Hyprland, Sway, River, Niri, and similar environments.

This should call the TypeWhisper CLI or D-Bus service.

Example Hyprland-style behavior:

```text
Key press:
  typewhisper record start

Key release:
  typewhisper record stop
```

This can work very well when the compositor supports release bindings. It should be documented as a strong manual setup option, not the only default path.

### 6. Focused-window listener

Keep the current focused-window listener only as a convenience.

It should not be treated as the Wayland global hotkey solution.

## Why evdev should be first for Wayland

Evdev is the best immediate backend because it fixes the reported bug directly and preserves TypeWhisper's existing interaction model.

| Backend | Works while another app is focused | Detects key press | Detects key release | Good for push-to-talk | Notes |
| --- | --- | --- | --- | --- | --- |
| SharpHook on X11 | Yes | Yes | Yes | Yes | Keep for X11. |
| SharpHook on Wayland | No | Only when focused | Only when focused | No | Current problem. |
| XDG GlobalShortcuts portal | Usually | Yes | Desktop-dependent | Maybe | Good fallback, not safest primary PTT path. |
| GNOME/KDE custom shortcut | Yes | Usually activation only | Usually no | No | Good for toggle. |
| Compositor binding | Yes | Yes | Depends on compositor | Sometimes | Strong manual path. |
| evdev | Yes | Yes | Yes | Yes | Best universal PTT backend, but needs input permission. |

## Important hotkey note

Ctrl+Shift+Space is not ideal for every backend.

With evdev, TypeWhisper can read the key event globally, but it does not consume the key event. The focused application may still receive the shortcut.

That means this can happen:

```text
User presses Ctrl+Shift+Space.
TypeWhisper starts dictation.
The focused app may also react to Ctrl+Shift+Space.
```

For compositor-owned shortcuts, Ctrl+Shift+Space is more reasonable because the compositor can own the binding.

For evdev mode, the setup wizard should recommend a dedicated hotkey.

Recommended evdev hotkeys:

```text
RightCtrl
Pause
ScrollLock
F8
F9
F12
F13 or another macro key
```

Allow Ctrl+Shift+Space, but show a warning.

Suggested warning:

```text
This backend can detect Ctrl+Shift+Space globally, but it cannot prevent the focused app from also receiving it. For push-to-talk, a dedicated key such as RightCtrl, Pause, ScrollLock, F12, or a macro key is recommended.
```

## Architecture to build

Create a backend-neutral hotkey layer.

```csharp
public interface IGlobalShortcutBackend
{
    string Name { get; }
    bool SupportsPressRelease { get; }
    bool SupportsToggleOnly { get; }

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    event EventHandler<GlobalShortcutEvent> ShortcutPressed;
    event EventHandler<GlobalShortcutEvent> ShortcutReleased;
}
```

Suggested event model:

```csharp
public enum GlobalShortcutEventType
{
    Pressed,
    Released,
    Activated
}

public sealed record GlobalShortcutEvent(
    GlobalShortcutEventType Type,
    string ShortcutId,
    string BackendName
);
```

Suggested backends:

```text
SharpHookGlobalShortcutBackend
EvdevGlobalShortcutBackend
PortalGlobalShortcutBackend
DesktopShortcutBackend
CompositorBindingBackend
FocusedWindowShortcutBackend
```

The existing HotkeyService should become a coordinator instead of directly owning one specific input library.

## Backend selector

Backend selection should be automatic but visible to the user.

Suggested logic:

```text
If XDG_SESSION_TYPE is x11:
  Use SharpHook.

If XDG_SESSION_TYPE is wayland and evdev is available:
  Use evdev.

If XDG_SESSION_TYPE is wayland and evdev is not available but portal is available:
  Use portal fallback.

If portal is unavailable:
  Offer desktop shortcut or compositor binding setup.

Always keep focused-window hotkey as a last-resort local shortcut.
```

The settings screen should show:

```text
Current session: Wayland
Active hotkey backend: evdev
Press/release support: available
Push-to-talk support: available
Permission status: input access granted
```

Or:

```text
Current session: Wayland
Active hotkey backend: portal
Press/release support: unavailable or unknown
Push-to-talk support: unavailable
Recommended action: enable evdev input access or configure compositor press/release bindings
```

## Recording control should be centralized

Borrow this from document 3.

Every trigger should call the same recording controller:

```text
UI button
Tray menu
SharpHook
Evdev
Portal
GNOME shortcut
KDE shortcut
Hyprland binding
Sway binding
CLI command
D-Bus method
```

All of them should flow into one state machine:

```text
Idle
  -> Recording
  -> Transcribing
  -> Injecting
  -> Idle
```

Do not let each backend manage recording separately.

## CLI and IPC recommendation

Also borrow this from document 3.

Add a small command surface:

```bash
typewhisper record start
typewhisper record stop
typewhisper record toggle
typewhisper record cancel
typewhisper status
```

These commands should communicate with the running app or daemon through a local IPC mechanism such as a Unix domain socket or D-Bus.

This makes all fallback paths easier:

```text
GNOME custom shortcut -> typewhisper record toggle
KDE shortcut -> typewhisper record toggle
Hyprland press binding -> typewhisper record start
Hyprland release binding -> typewhisper record stop
Sway binding -> typewhisper record toggle or start/stop where possible
Scripts -> typewhisper record start/stop/toggle
```

## Idempotent commands

The recording commands should be safe to call repeatedly.

```text
record start while already recording:
  Return OK and do nothing.

record stop while idle:
  Return OK and do nothing.

record toggle while idle:
  Start recording.

record toggle while recording:
  Stop recording.

record cancel while idle:
  Return OK and do nothing.
```

This prevents stuck states caused by repeated key events, compositor quirks, app restarts, or delayed release events.

## Setup wizard

Add a Linux hotkey setup wizard.

The wizard should detect:

```text
XDG_SESSION_TYPE
WAYLAND_DISPLAY
DISPLAY
XDG_CURRENT_DESKTOP
DESKTOP_SESSION
Input-device access
Portal availability
Compositor name when available
```

Then it should recommend the best backend.

Suggested flow:

```text
1. Detect session type.
2. Detect whether the current backend can capture global press and release.
3. Ask the user to press the configured shortcut.
4. Ask the user to release the configured shortcut.
5. Confirm whether press and release were detected.
6. Show whether toggle, push-to-talk, and hybrid are supported.
7. If unsupported, offer the next-best backend or setup instructions.
```

## Permissions UX

For evdev, be clear and honest.

Suggested wording:

```text
To detect push-to-talk while another app is focused, TypeWhisper needs permission to read keyboard device events. This allows TypeWhisper to detect your configured shortcut globally. TypeWhisper should only track the configured shortcut state, not record typed text.
```

Show the current status:

```text
Input access: granted
Input access: not granted
Input access: unknown
```

Offer copyable setup commands, but do not silently modify system permissions without explaining the security implication.

## Implementation phases

### Phase 1: Refactor the current hotkey service

Goal: make the hotkey system backend-neutral.

Tasks:

```text
Create IGlobalShortcutBackend.
Move the current SharpHook logic into SharpHookGlobalShortcutBackend.
Keep existing HotkeyService events stable.
Add backend capability flags.
Add logging that shows the active backend.
```

Expected result:

```text
No behavior change yet.
X11 still works through SharpHook.
Wayland still has the old limitation until the next phase.
```

### Phase 2: Add evdev backend

Goal: fix global push-to-talk on Wayland for users who grant input access.

Tasks:

```text
Discover keyboard devices under /dev/input/by-id or /dev/input/event*.
Read Linux input_event records.
Track modifier state.
Match the configured shortcut.
Emit ShortcutPressed and ShortcutReleased.
Handle device disconnect/reconnect.
Ignore repeated press events while already held.
```

Expected result:

```text
Wayland users with input access can trigger TypeWhisper while another app is focused.
Push-to-talk and hybrid mode work.
```

### Phase 3: Add backend selection and diagnostics

Goal: choose the right backend automatically and make the choice visible.

Tasks:

```text
Detect X11 vs Wayland.
Detect evdev permission.
Detect portal availability.
Select the best backend.
Show backend status in settings.
Add a shortcut test panel.
```

Expected result:

```text
Users can see why a shortcut does or does not work.
Support/debugging becomes much easier.
```

### Phase 4: Add CLI and IPC

Goal: support compositor, desktop shortcut, and script-based triggers.

Tasks:

```text
Add typewhisper record start.
Add typewhisper record stop.
Add typewhisper record toggle.
Add typewhisper record cancel.
Add typewhisper status.
Connect CLI to the running app through Unix socket or D-Bus.
Make all commands idempotent.
```

Expected result:

```text
Desktop shortcuts and compositor bindings can control TypeWhisper reliably.
Users who do not want evdev permissions still have a usable fallback.
```

### Phase 5: Add portal and desktop fallback support

Goal: improve lower-permission Wayland support.

Tasks:

```text
Implement XDG GlobalShortcuts portal backend where available.
Add GNOME custom shortcut setup instructions.
Add KDE shortcut setup instructions.
Add Hyprland and Sway examples.
Clearly mark which paths support push-to-talk and which are toggle-only.
```

Expected result:

```text
More Wayland users can use TypeWhisper without joining the input group.
Push-to-talk users still have evdev or compositor release-binding options.
```

## Suggested first PRs

Use document 2's simpler structure for the first implementation steps.

```text
PR 1: Extract SharpHook into SharpHookGlobalShortcutBackend.
PR 2: Add backend-neutral hotkey interface and event model.
PR 3: Add EvdevGlobalShortcutBackend.
PR 4: Add backend selector for X11 vs Wayland.
PR 5: Add settings/status UI for active backend and permissions.
PR 6: Add manual shortcut test panel.
PR 7: Add CLI commands and local IPC.
PR 8: Add portal/compositor/desktop fallback docs and optional integrations.
```

## What not to do first

Do not start by rewriting the full app around portals.

Do not make the portal the only Wayland solution.

Do not assume GNOME/KDE custom shortcuts are enough for push-to-talk.

Do not keep SharpHook as the Wayland global hotkey path.

Do not silently request broad input permissions without user-facing explanation.

Do not let each backend independently control recording state.

## Success criteria

The first successful version should pass these tests.

### X11

```text
TypeWhisper focused:
  Shortcut works.

Text editor focused:
  Shortcut works.

Push-to-talk:
  Press starts recording.
  Release stops recording.
```

### Wayland with evdev permission

```text
TypeWhisper focused:
  Shortcut works.

Text editor focused:
  Shortcut works.

Browser focused:
  Shortcut works.

Terminal focused:
  Shortcut works.

Push-to-talk:
  Press starts recording.
  Release stops recording.

Hybrid:
  Press/release behavior works according to TypeWhisper settings.
```

### Wayland without evdev permission

```text
The app does not pretend global push-to-talk is available.
The UI explains the limitation.
The user can choose portal, desktop shortcut, compositor binding, or CLI fallback.
Toggle mode remains usable where possible.
```

## Final recommendation

Build the next version around this combined plan:

```text
Main recommendation:
  Document 1

Implementation roadmap:
  Document 2

Architecture pieces to borrow:
  Document 3 daemon/CLI/state-machine/setup-wizard ideas
```

The first real fix should be the evdev backend because it solves the exact foreground-only hotkey failure and preserves TypeWhisper's push-to-talk and hybrid modes.

After that, add portal, desktop shortcut, compositor binding, CLI, and D-Bus support so users have safer lower-permission options when they do not want to grant input-device access.
