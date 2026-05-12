# Phase 3 — Implementation Plan

The phase doc asks for two deliverables: a portal backend and a Settings → Shortcuts status panel. The status panel is independently shippable and high-value (it makes the work in Phases 1–2 discoverable to users); the portal backend requires adding a D-Bus client dependency and implementing a non-trivial signal-based session protocol.

Scope of this iteration:

1. **Settings → Shortcuts status panel** — surfaces backend state, capability flags, session type, and an "input group" banner with copy-to-clipboard. Adds the evdev opt-out toggle backed by `AppSettings.WaylandEvdevHotkeysEnabled` (added in Phase 2).
2. **Portal backend stub** (`XdgPortalGlobalShortcutsBackend`) — implements `IGlobalShortcutBackend` with `IsAvailable() => false` plus a `// TODO` block describing the D-Bus session protocol. `BackendSelector` is updated to try portal between evdev and SharpHook on Wayland so the wiring is in place when the implementation lands. This keeps the architecture honest without shipping a half-working portal path that misleads users.
3. **Capability-mismatch surfacing** — uses `HotkeyService.BackendRequiresToggleMode` (added in Phase 1) to drive a warning when the user has Mode = PushToTalk/Hybrid but the active backend can only deliver presses.

Deferred to a follow-up:

- Real D-Bus portal session implementation (CreateSession / BindShortcuts / Activated signal). Needs Tmds.DBus or equivalent dependency. Skeleton plus TODO block lands now so the work is ready to pick up.
- "Test your shortcut" modal — depends on the shortcut backend events being exposed at the ViewModel layer; this needs careful threading work and isn't blocking for the status panel.
- Backend switcher chooser — same shape as above; depends on the test modal landing first.

## New files

1. `src/TypeWhisper.Linux/Services/Hotkey/Portal/XdgPortalGlobalShortcutsBackend.cs` — interface implementation with `IsAvailable() => false` (no portal available yet); when the real impl lands it flips to a true availability probe. RegistrationResult sets `RequiresToggleMode: true` so the UI knows to surface the capability mismatch.

## Modified files

1. `src/TypeWhisper.Linux/Services/Hotkey/BackendSelector.cs` — slot portal between evdev and SharpHook on Wayland.
2. `src/TypeWhisper.Linux/ServiceRegistrations.cs` — register portal backend.
3. `src/TypeWhisper.Linux/Services/HotkeyService.cs` — expose `ActiveBackendId` so the UI can surface it.
4. `src/TypeWhisper.Linux/ViewModels/Sections/ShortcutsSectionViewModel.cs` — adds: active backend display, session type, capability summary, input-group banner state, evdev opt-out toggle (bound to `WaylandEvdevHotkeysEnabled`), capability-mismatch warning, copy-`sudo`-command command.
5. `src/TypeWhisper.Linux/Views/Sections/ShortcutsSection.axaml` — renders the status panel + banner.

## Tests

- `ShortcutsSectionViewModelTests` (existing) extended with: capability mismatch shows when Mode != Toggle and backend requires toggle; input-group banner shows only on Wayland when user isn't in input group.

## Risks/mitigations

- **UI not visually testable here.** I can verify build + bindings compile and viewmodel logic via unit tests. Visual review is the user's responsibility.
- **Portal stub returning `IsAvailable() => false`.** Looks like a regression in availability detection, but is honest about the missing D-Bus impl. The TODO block documents the protocol so the next iteration knows where to plug in.
