# Phase 1 — Implementation Plan

## Approach

Refactor `HotkeyService` into a coordinator over an `IGlobalShortcutBackend`. The SharpHook event-handling code moves into `SharpHookGlobalShortcutBackend`. `HotkeyService` keeps the public surface intact (every property, event, method that callers use today).

## New files

1. `src/TypeWhisper.Linux/Services/Hotkey/GlobalShortcutSet.cs` — immutable record carrying the eight configured bindings (Dictation, PromptPalette, Recent, CopyLast, TransformSelection, Cancel) plus `Mode` and `IsCancelEnabled`.
2. `src/TypeWhisper.Linux/Services/Hotkey/GlobalShortcutRegistrationResult.cs` — result record per phase doc (Success, BackendId, UserMessage, RequiresToggleMode, TroubleshootingCommand).
3. `src/TypeWhisper.Linux/Services/Hotkey/IGlobalShortcutBackend.cs` — interface per phase doc.
4. `src/TypeWhisper.Linux/Services/Hotkey/ShortcutMatcher.cs` — static helper: pure `(KeyCode, ModifierMask) → match-kind` plus a `ModifiersMatch` helper. Pure stateless logic so the evdev backend (Phase 2) can reuse it.
5. `src/TypeWhisper.Linux/Services/Hotkey/SharpHookGlobalShortcutBackend.cs` — owns `TaskPoolGlobalHook`, current `GlobalShortcutSet` snapshot, repeat-guard flags, and `_keyDownTime`. Subscribes to SharpHook press/release, routes through `ShortcutMatcher`, applies Mode logic (Toggle/PushToTalk/Hybrid + 600ms threshold), raises typed events.
6. `src/TypeWhisper.Linux/Services/Hotkey/BackendSelector.cs` — for Phase 1 unconditionally returns the SharpHook backend.

## Modified files

1. `src/TypeWhisper.Linux/Services/HotkeyService.cs` — becomes coordinator:
   - Keeps mode/binding state (the bindings are still the source of truth for the `Current*HotkeyString` properties).
   - On `Initialize()`: resolves backend, subscribes to its events, pushes initial `GlobalShortcutSet` via `RegisterAsync`.
   - On any `TrySet*` / `SetHotkey` / `Mode=` / `IsCancelShortcutEnabled=`: rebuilds `GlobalShortcutSet` and pushes it to the active backend (fire-and-forget after Initialize).
   - Public surface stays identical (every event, every property, every method).
   - `ModifiersMatch` kept as a forwarding wrapper so the existing test still compiles.
2. `src/TypeWhisper.Linux/ServiceRegistrations.cs` — register `SharpHookGlobalShortcutBackend` + `BackendSelector` alongside `HotkeyService`.

## Tests

- Existing `HotkeyServiceTests` must still pass. The parameterless `new HotkeyService()` ctor stays (used by tests).
- Add a unit test `ShortcutMatcherTests` for `ShortcutMatcher.Match(...)` with the default `Ctrl+Shift+Space` set — asserts the press is identified as `ShortcutMatchKind.Dictation`.

## Risks/mitigations

- **Threading**: SharpHook delivers on its hook thread. The backend re-raises events on that thread (same as today). `HotkeyService.OnBackend*` simply forwards; existing code in `App.axaml.cs` already marshals to UI thread where needed.
- **State sync**: When a `TrySet*` is called *before* `Initialize`, the next `Initialize` registers a set that already reflects it (same behavior as today). When called *after*, we push an updated set via `_backend.RegisterAsync` (re-register is idempotent — it just updates the snapshot).
- **Test ctor**: `new HotkeyService()` must not crash. We instantiate a default `BackendSelector` that owns a lazy `SharpHookGlobalShortcutBackend` — `TaskPoolGlobalHook` is constructed only when the backend is first resolved, but since `_hook = new()` is a field initializer in the existing code and that works, this should too.

## Exit criteria (per phase doc)

- `git diff` confined to `Services/Hotkey/`, `Services/HotkeyService.cs`, `ServiceRegistrations.cs`, and the new test file.
- `dotnet build` passes.
- `dotnet test tests/TypeWhisper.Linux.Tests` passes.
- No behavior change (the unit tests verifying parsing logic remain valid).
