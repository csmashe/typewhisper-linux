# Phase 3 — Portal Backend + Settings Status UI

Status: blocked on Phase 2
Depends on: Phase 2 (evdev backend, idempotency, interface)
Unblocks: Phase 4 (CLI infrastructure rests on a clean settings panel)

## Goal

Two deliverables in one phase, both required for users who don't (or can't) use evdev:

1. **`XdgPortalGlobalShortcutsBackend`** — toggle-only global shortcuts via `org.freedesktop.portal.GlobalShortcuts`. Works without `/dev/input` access. PushToTalk and Hybrid degrade to Toggle on this backend (with a clear UI note).
2. **Settings → Shortcuts status panel** — shows the active backend, what it supports, what permission state it's in, and a "test your shortcut" button. This is what users will look at when something's broken; it pays for itself in support load.

## Scope

In scope:

- `XdgPortalGlobalShortcutsBackend` using D-Bus to `org.freedesktop.portal.Desktop`. Probe `org.freedesktop.portal.GlobalShortcuts` exists; if not, `IsAvailable()` returns false.
- BackendSelector: Wayland → evdev → portal → SharpHook (focused-window degraded).
- Settings → Shortcuts panel with the status display and shortcut-test button (described below).
- "Capability mismatch" notice in the UI when the active backend doesn't support press/release and the user has Mode = PushToTalk or Hybrid.

Out of scope:

- CLI / IPC (Phase 4+).
- DE-specific helpers (Phase 6).
- Portal *keyboard injection* (Remote Desktop portal for typing). That's a different problem (text insertion) and outside the hotkey work — TypeWhisper already has `wtype`/`ydotool`/clipboard fallback for output.

## Files

### New

```
src/TypeWhisper.Linux/Services/Hotkey/Portal/XdgPortalGlobalShortcutsBackend.cs
src/TypeWhisper.Linux/Services/Hotkey/Portal/PortalSessionManager.cs
src/TypeWhisper.Linux/Services/Hotkey/Portal/PortalDBusInterfaces.cs     # generated/handwritten
src/TypeWhisper.Linux/Views/Settings/ShortcutStatusView.axaml
src/TypeWhisper.Linux/Views/Settings/ShortcutStatusView.axaml.cs
src/TypeWhisper.Linux/ViewModels/Settings/ShortcutStatusViewModel.cs
```

### Modified

```
src/TypeWhisper.Linux/Services/Hotkey/BackendSelector.cs
src/TypeWhisper.Linux/ServiceRegistrations.cs
src/TypeWhisper.Linux/Views/Settings/ShortcutsSettingsView.axaml      # embed status panel
```

## Portal backend — implementation notes

### D-Bus library

Use `Tmds.DBus.Protocol` (already in the .NET Linux ecosystem; the existing TypeWhisper code may already reference it — check `*.csproj` before adding a duplicate).

### Session model

The portal works in three steps:

1. `CreateSession` on `org.freedesktop.portal.GlobalShortcuts` → returns a session handle.
2. `BindShortcuts(session, [(id, description, preferred_trigger)])` → user sees a desktop dialog and accepts/rejects.
3. Subscribe to `Activated(session, shortcut_id, timestamp, options)` signal. `Deactivated` exists in the spec but is unreliable on KDE Plasma 6 and GNOME — treat all bindings as toggle.

Persist the session handle across app restarts: the portal lets you re-attach to an existing session if the user already approved. Store the handle in TypeWhisper's settings DB. On startup, try to reattach; if it fails, prompt for re-binding.

### Shortcut IDs

Stable per-action IDs so reattach works:

```
typewhisper.dictation.toggle
typewhisper.prompt-palette
typewhisper.recent
typewhisper.copy-last
typewhisper.transform-selection
```

### Capability flags

```csharp
public bool SupportsPressRelease => false;
```

In `RegisterAsync`'s result:

```csharp
new GlobalShortcutRegistrationResult(
    Success: true,
    BackendId: "linux-xdg-portal",
    UserMessage: "Global shortcuts work through your desktop's shortcut system. Push-to-talk and Hybrid modes are not available on this backend — see Settings.",
    RequiresToggleMode: true,
    TroubleshootingCommand: null);
```

`RequiresToggleMode = true` tells the UI to display the capability mismatch banner if the user has `Mode = PushToTalk` or `Mode = Hybrid` in settings.

## Settings → Shortcuts status panel

### Layout sketch

```
┌─ Global Shortcuts ─────────────────────────────────────┐
│                                                         │
│  Active backend: Evdev (system input device)            │
│  Display server: Wayland                                │
│  Press/release support: yes                             │
│  Push-to-talk support: yes                              │
│  Permission status: input access granted                │
│                                                         │
│  [ Test your shortcut ]   [ Switch backend… ]           │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

When the active backend is portal:

```
│  Active backend: XDG Desktop Portal                     │
│  Display server: Wayland                                │
│  Press/release support: no                              │
│  Push-to-talk support: not available                    │
│  Permission status: user-approved per shortcut          │
│                                                         │
│  ⚠ Your dictation Mode is set to Push-to-talk, but      │
│  this backend only supports Toggle. Either:             │
│    • Switch Mode to Toggle in the dictation settings    │
│    • Enable evdev (Add user to input group)             │
│                                                         │
│  [ Test your shortcut ]   [ Switch backend… ]           │
```

### Test-your-shortcut button

When clicked, opens a modal:

```
Press your dictation shortcut.

      ◯ Waiting…

(Cancel)
```

State machine for the modal:

- Wait for `DictationStartRequested` (any of `DictationToggleRequested` / `DictationStartRequested`) — show "✓ Press detected".
- Wait for `DictationStopRequested` if the backend `SupportsPressRelease` — show "✓ Release detected".
- After 2s of inactivity post-press, show summary:
  - "Toggle mode is supported."
  - If release was detected: "Push-to-talk and Hybrid modes are supported."
  - If not: "Push-to-talk and Hybrid are not available on this backend."

Critically: while the test modal is open, the dictation flow is suppressed. The modal owns the events. Wire this by subscribing directly to `IGlobalShortcutBackend` events (not the `HotkeyService` re-raises) and consuming them.

### Switch-backend button

Opens a chooser listing the available backends from `BackendSelector.GetAvailable()`. Each entry shows:

- Name
- Capability summary (Toggle / PTT / Hybrid)
- Permission requirements (e.g., "Requires `input` group membership")

Selecting an entry calls `BackendSelector.SetPreferred(id)`, then unregisters the current backend and registers the new one. Persist preference in settings.

## DI changes

```csharp
services.AddSingleton<XdgPortalGlobalShortcutsBackend>();
services.AddSingleton<PortalSessionManager>();
services.AddSingleton<ShortcutStatusViewModel>();

// BackendSelector.Resolve()
//   x11     -> SharpHook
//   wayland -> evdev (if available)
//           -> portal (if available + user accepted bindings before, or first run)
//           -> SharpHook (focused-window degraded)
```

## Exit criteria

### Portal backend

- On Fedora GNOME Wayland with portal available and user *not* in `input` group:
  - First run: portal dialog appears asking the user to bind shortcuts. After acceptance, `Ctrl+Shift+Space` (or configured chord) toggles dictation globally.
  - Mode = Toggle works. Mode = PushToTalk shows the capability-mismatch warning.
- On KDE Plasma 6 Wayland: same.
- On a wlroots-only system without a portal (Sway with no `xdg-desktop-portal-wlr` global-shortcuts support): `IsAvailable()` returns false, selector falls through to focused-window degraded.

### Settings panel

- Active backend, capability summary, and permission status visible from Settings → Shortcuts.
- Test-your-shortcut modal correctly identifies press-only vs press+release backends.
- Capability mismatch banner appears when Mode + backend disagree and links to remediation.
- Backend switcher persists the user's choice and applies it without restart.

### Stress / regression

- Repeat Phase 2's stress tests on the portal path (toggle only). 50 rapid presses → no stuck state.
- Disconnect and reconnect the portal session (kill `xdg-desktop-portal`, restart): backend recovers and re-registers without app restart, or surfaces a clear error to the status panel.

## What this phase explicitly does NOT do

- Does NOT add CLI commands or IPC. Phase 4.
- Does NOT auto-create GNOME/KDE custom keyboard shortcuts. Phase 6.
- Does NOT make portal the *default* backend on Wayland — evdev still wins when available.
- Does NOT try to work around the portal's missing `Deactivated` signal. Treat portal as toggle-only and surface that honestly.

## Risks

| Risk | Mitigation |
|---|---|
| Portal `Deactivated` works on one desktop but not another → inconsistent PTT. | Don't trust it at all. `SupportsPressRelease = false` always. |
| User on Wayland, no portal, no `input` group. | Already covered by selector falling through to SharpHook. Status panel makes the situation visible. Phase 6 will improve this with DE-specific custom-shortcut helpers. |
| Portal session lost (portal process restarted). | Catch the D-Bus disconnect, mark backend unhealthy in status panel, attempt one reconnect with backoff, then surface error. |
| Two TypeWhisper instances both binding the same shortcut. | Phase 4 introduces single-instance enforcement. Until then, document "don't run two copies" and let the second instance's binding fail gracefully. |
| User picks PortalBackend manually via the switcher despite being in `input` group. | Honor the choice. Persist it. Show capability summary so they know what they're giving up. |
