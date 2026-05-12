# Phase 1 — Interface Extraction

Status: ready to start
Depends on: nothing
Unblocks: Phase 2 (evdev backend) and every subsequent phase

## Goal

Move the existing SharpHook hotkey logic behind an `IGlobalShortcutBackend` interface, with **zero behavior change**. This is plumbing only — it lets Phase 2 swap in evdev without touching the orchestrator, overlay, or settings code.

## Scope

In scope:

- Define `IGlobalShortcutBackend`.
- Define `BackendSelector` (returns SharpHook for now — always).
- Wrap the existing `HotkeyService` body in `SharpHookGlobalShortcutBackend`.
- Update DI registration to construct via the selector.
- Refactor `HotkeyService` to be a coordinator that subscribes to the active backend and re-raises its events. The public surface (`DictationToggleRequested`, `Mode`, `TrySetHotkeyFromString`, etc.) does not change — callers like `App.axaml.cs:90` and `DictationOrchestrator.cs:25` stay as they are.

Out of scope:

- Any new backends (evdev / portal / CLI). Those are Phase 2+.
- Any settings UI changes.
- Any new hotkey defaults.

## Files

### New

```
src/TypeWhisper.Linux/Services/Hotkey/IGlobalShortcutBackend.cs
src/TypeWhisper.Linux/Services/Hotkey/SharpHookGlobalShortcutBackend.cs
src/TypeWhisper.Linux/Services/Hotkey/BackendSelector.cs
src/TypeWhisper.Linux/Services/Hotkey/GlobalShortcutRegistrationResult.cs
src/TypeWhisper.Linux/Services/Hotkey/GlobalShortcutSet.cs
```

### Modified

```
src/TypeWhisper.Linux/Services/HotkeyService.cs         # becomes coordinator
src/TypeWhisper.Linux/ServiceRegistrations.cs:69        # register backends + selector
```

## Interface

```csharp
public interface IGlobalShortcutBackend : IAsyncDisposable
{
    string Id { get; }                    // "linux-sharphook", "linux-evdev", ...
    string DisplayName { get; }
    bool SupportsPressRelease { get; }    // true for SharpHook + evdev, false for portal
    bool IsAvailable();                   // can this backend run right now?

    Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct);

    Task UnregisterAsync(CancellationToken ct);

    event EventHandler? DictationToggleRequested;
    event EventHandler? DictationStartRequested;
    event EventHandler? DictationStopRequested;
    event EventHandler? PromptPaletteRequested;
    event EventHandler? TransformSelectionRequested;
    event EventHandler? RecentTranscriptionsRequested;
    event EventHandler? CopyLastTranscriptionRequested;
    event EventHandler? CancelRequested;
    event EventHandler<string>? Failed;
}

public sealed record GlobalShortcutRegistrationResult(
    bool Success,
    string BackendId,
    string? UserMessage,
    bool RequiresToggleMode,           // backend can't deliver release events
    string? TroubleshootingCommand);   // e.g., "sudo usermod -aG input $USER"
```

The `GlobalShortcutSet` is a struct/record holding the eight configured shortcuts that `HotkeyService` already tracks (toggle, prompt palette, recent, copy-last, transform-selection, plus mode).

## Refactor sketch — HotkeyService.cs

Current state: `HotkeyService.cs:24-37` instantiates `TaskPoolGlobalHook _hook = new();` directly. The class owns the SharpHook lifecycle, key matching, mode state, and the public events at `:65-73`.

After refactor:

- Move the SharpHook-specific code (hook lifecycle, `OnKeyPressed` at `:364`, `OnKeyReleased` at `:486`, libuiohook keycode matching) into `SharpHookGlobalShortcutBackend`.
- `HotkeyService` keeps:
  - Public events (re-raised from the active backend).
  - `Mode` property and the mode state machine.
  - `TrySetHotkeyFromString` / `CurrentHotkeyString` / etc. — these stay because they parse user input and expose the current binding string for the UI.
  - `Initialize()` — now calls `BackendSelector.Resolve()` and `RegisterAsync`.
  - `Dispose` — disposes the active backend.

The key-matching logic that turns SharpHook `(KeyCode, ModifierMask)` events into "is this our configured chord?" needs to be available to both backends. Extract it into a static helper:

```csharp
internal static class ShortcutMatcher
{
    public static ShortcutMatch? Match(
        KeyCode key,
        ModifierMask mods,
        bool pressed,
        GlobalShortcutSet shortcuts);
}
```

Phase 2's evdev backend will translate kernel `KEY_*` codes to SharpHook `KeyCode` values and then call the same matcher — so the configured-chord parsing and matching stays single-sourced.

## DI changes

```csharp
// ServiceRegistrations.cs — replace line 69
services.AddSingleton<SharpHookGlobalShortcutBackend>();
services.AddSingleton<BackendSelector>();
services.AddSingleton<HotkeyService>();   // unchanged registration, new internals
```

`BackendSelector` for Phase 1 just returns `SharpHookGlobalShortcutBackend` regardless of session type. It becomes interesting in Phase 2.

## Exit criteria

- `git diff` shows zero changes outside `src/TypeWhisper.Linux/Services/Hotkey/`, `HotkeyService.cs`, and `ServiceRegistrations.cs`.
- Existing tests pass. If there are no hotkey-specific tests today (likely), add at least one unit test asserting `ShortcutMatcher.Match` produces the right `ShortcutMatch` for the current default binding `Ctrl+Shift+Space`.
- Manual smoke test on the developer machine (X11 or Wayland): start TypeWhisper, press the configured hotkey while TypeWhisper is focused — dictation starts. Same as today. Nothing else changes.
- No log line says "backend selected" yet at user-visible level — there's still only one backend.

## What this phase explicitly does NOT do

- Does NOT fix the Wayland focused-only bug. That's Phase 2.
- Does NOT add any settings UI for backend selection. That's Phase 3.
- Does NOT change any default hotkey. The default stays `Ctrl+Shift+Space`.
- Does NOT touch `DictationOrchestrator.cs` — it still subscribes to the same `HotkeyService` events.
- Does NOT introduce any new external dependencies.

## Risks

| Risk | Mitigation |
|---|---|
| The `HotkeyService` public surface is large and used by `App.axaml.cs` in many places (lines 92-128). | Keep every public method on `HotkeyService`. Only the *internal* implementation of those methods changes — they now route through the active backend. |
| SharpHook's `KeyboardHookEventArgs` is libuiohook-specific. Lifting key matching out of `HotkeyService` means the matcher can't depend on SharpHook types. | Define the matcher in terms of a backend-neutral `(KeyCode, ModifierMask)` tuple. SharpHook fills it from its event args; evdev (Phase 2) fills it from `LinuxKeyMap`. |
| Threading: SharpHook delivers events on a hook thread; `HotkeyService` already marshals to the dispatcher via existing code. | Preserve current marshaling behavior in `SharpHookGlobalShortcutBackend`. Don't introduce new threading semantics. |
