# Linux Port Hookup Gap Closure

## Context

This repository is a Linux port of the Windows TypeWhisper app. The Linux app now has the main runtime hookup path working: dictation, profile resolution, prompt/action execution, plugin events, post-processing, autostart, audio ducking, media pause, and translation are all wired into the Linux flow.

This document is now the active backlog for the remaining Windows-to-Linux parity work. The focus has shifted from basic hookup work to the smaller set of features that still exist on Windows but do not yet look or behave the same on Linux.

## Porting Rules

These rules apply to the remaining Linux parity work:

1. Treat Windows as the source of truth for behavior.
2. Before writing new Linux code, inspect the Windows implementation and compare it to the current Linux branch.
3. Do not recreate code if the needed behavior already exists in the Linux branch, in `main`, or in shared/core code.
4. Prefer direct ports and narrow adaptations over Linux-specific redesigns.
5. Only change behavior when the Windows mechanism itself does not work on Linux.
6. If Linux requires an adaptation, keep the user-visible behavior as close to Windows as practical.
7. Do not add new Linux-only features while parity work is still incomplete.
8. Close one parity section fully before moving to the next section.

## Porting Workflow

Use this workflow for each remaining parity section:

1. Read the Windows implementation for the feature end to end.
2. Inspect the current Linux implementation and identify the exact parity gaps.
3. Check whether the missing behavior already exists elsewhere in the repo before adding code.
4. Port the Windows behavior directly where possible.
5. Adapt only the platform-specific pieces that cannot work on Linux.
6. Remove Linux-only deviations when they are not required for platform compatibility.
7. Verify the section with build/test coverage and any targeted manual validation that applies.
8. Update this document when the section is complete so the next active section is explicit.

## Completed Work

### 1. Runtime event bus is wired

Linux now publishes runtime plugin events from dictation:

- `RecordingStartedEvent`
- `RecordingStoppedEvent`
- `TranscriptionCompletedEvent`
- `TranscriptionFailedEvent`
- `TextInsertedEvent`
- `ActionCompletedEvent`

Result:

- event-driven plugins are no longer loaded-only dead weight
- history, action execution, and downstream plugin logic now receive real runtime context

### 2. Profiles are resolved and applied during dictation

Linux dictation now resolves the active window context and applies profile-bound overrides for:

- language
- task
- model override
- prompt action
- translation target
- profile metadata in history and plugin events

Result:

- profiles are now functional runtime behavior, not just stored configuration
- saved history records include `ProfileName`
- profile editing UI supports real selection/edit/save workflows

### 3. Post-processing pipeline is integrated

Linux now uses `IPostProcessingPipeline` instead of an ad hoc post-processing path, including:

- app formatting
- dictionary corrections
- snippet expansion
- vocabulary boosting
- plugin post-processors
- prompt-driven LLM processing
- translation

Result:

- Linux post-processing behavior now tracks the shared pipeline model used by the Windows app
- plugin post-processors and translation hooks are no longer dead abstractions

### 4. Prompt actions and action plugins are operational

Linux now supports:

- profile-bound prompt actions through enabled LLM providers
- routing processed output into bound action plugins
- `ActionCompletedEvent` publishing for action execution results

Result:

- the Prompts section is no longer CRUD-only
- action plugins are no longer loaded-but-unreachable in Linux dictation

### 5. Settings shell cleanup and Linux-native platform wiring are done

Completed shell/platform cleanup:

- removed the dead dedicated Linux settings window path
- tray settings now intentionally open the main window and navigate to settings
- exposed `StartupService` in Linux General settings
- implemented Linux audio ducking via `pactl`
- implemented Linux media pause/resume via `playerctl`

Result:

- Linux now has one intentional settings shell model
- autostart, ducking, and media pause are no longer misleading settings-only controls

### 6. Prompt palette workflow is now wired on Linux

Linux now supports the separate Windows-style prompt palette flow through the Linux shell, including:

- prompt palette hotkey wiring through the shared `PromptPaletteHotkey` setting
- a Linux prompt palette window for selected text
- selected-text capture outside dictation
- prompt-action execution without starting a dictation session
- action-plugin routing and normal text insertion from the prompt palette path
- user-facing warning dialogs for prompt-provider and prompt-processing failures

Result:

- Linux prompt actions are no longer limited to dictation or profile-bound automation
- the Linux prompt palette workflow now behaves much closer to the Windows flow
- Phase 8 is complete and no longer part of the active parity backlog

### 7. Whisper mode is now implemented in Linux recording

Linux now applies the Windows whisper-mode capture behavior in the Linux recording path, including:

- runtime use of the shared `WhisperModeEnabled` setting during Linux recording
- runtime use of profile `WhisperModeOverride` with the same profile-over-global precedence model used on Windows
- Linux audio capture gain adjustment when whisper mode is active
- Linux settings/profile UI wiring so the existing shared whisper-mode fields are no longer dead configuration

Result:

- whisper mode now changes Linux recording behavior in a meaningful, testable way
- profile whisper overrides are now functional runtime behavior on Linux
- Phase 9 is complete and no longer part of the active parity backlog

### 8. Profile live-context tooling is now implemented on Linux

Linux now supports the main Windows-style live profile authoring helpers, including:

- live current-app context in the Profiles section
- live matched-profile display in the Profiles section
- quick-add helpers for the current app and current URL/domain
- a separate Linux live-context window
- Linux browser URL/domain capture paths for X11 and a Wayland-capable AT-SPI path

Result:

- Linux profile authoring is now much closer to the Windows workflow
- users can build and validate profiles with much less manual trial and error
- remaining risk is primarily manual browser/compositor validation, not a known missing hookup
- Phase 10 is complete for now and no longer part of the active parity backlog

### 9. Overlay-style dictation presentation is now implemented on Linux

Linux now supports a dedicated Windows-style dictation overlay flow instead of relying only on the main shell, including:

- a separate Linux overlay window for in-session dictation feedback
- recording, processing, and transient completion/error presentation in that surface
- active-profile and active-app display in the overlay
- live recording timer and audio-level presentation during dictation
- overlay positioning through the shared `OverlayPosition` setting
- X11/XWayland focus handoff back to the original target window before text insertion

Result:

- Linux dictation now has a dedicated in-session presentation layer much closer to the Windows model
- the overlay no longer regresses normal text insertion in the validated Linux environment
- Phase 11 is complete and no longer part of the active parity backlog

### 10. Partial transcript / live update parity is now implemented on Linux

Linux now supports Windows-style partial transcript updates during recording, including:

- polling the live Linux recording buffer while dictation is active
- stabilized incremental transcript updates during recording
- publishing `PartialTranscriptionUpdateEvent` from the Linux dictation flow
- support for provider-specific progress callbacks through `TranscribeStreamingAsync`
- Linux overlay display of the current partial transcript during recording

Result:

- Linux plugins can now receive incremental transcript updates during dictation
- Linux dictation UI now exposes live transcript feedback instead of only lifecycle/status state
- Phase 12 is complete and no longer part of the active parity backlog

## Remaining Gaps

No known Windows-to-Linux runtime hookup gaps remain in this backlog.

## Updated Execution Strategy

The foundational hookup work is complete. This backlog no longer has an active runtime parity section and should only be extended if a new Windows/Linux behavior gap is identified.

There is no current Phase 12 successor in this document.

## Suggested Issue Breakdown

There are no remaining GitHub issues implied by this runtime-hookup backlog at the moment.

## Verification

### Current baseline

These commands currently pass after the completed hookup work:

```bash
dotnet test tests/TypeWhisper.Linux.Tests/TypeWhisper.Linux.Tests.csproj -nologo
dotnet build TypeWhisper.slnx -nologo
```

### Remaining focused validation

1. Verify overlay/session feedback on Linux remains stable across target desktop environments and compositors.
2. Verify partial transcript events are emitted and consumed correctly by Linux plugins.

## Recommendation

This backlog is now effectively closed. The next work should come from newly discovered parity gaps or from separate UX/manual-validation follow-up items rather than from the original Linux hookup backlog.
