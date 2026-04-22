# Linux Port Hookup Gap Closure

## Context

This repository is a Linux port of the Windows TypeWhisper app. The Linux app now has the main runtime hookup path working: dictation, profile resolution, prompt/action execution, plugin events, post-processing, autostart, audio ducking, media pause, and translation are all wired into the Linux flow.

This document is now the active backlog for the remaining Windows-to-Linux parity work. The focus has shifted from basic hookup work to the smaller set of features that still exist on Windows but do not yet look or behave the same on Linux.

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

## Remaining Gaps

### 1. Prompt palette workflow is still Windows-only

Windows supports a separate prompt palette flow and prompt-palette hotkey. Linux currently supports prompt actions only as part of dictation and profile-bound automation.

Still missing on Linux:

- prompt palette hotkey wiring
- a Linux prompt palette UI/workflow for selected text
- parity with the Windows "run prompt without dictation" flow

Impact:

- Linux has the prompt engine, but not the full Windows prompt workflow surface

### 2. Whisper mode is still not implemented in Linux recording

The shared setting and profile override fields still exist, but Linux `AudioRecordingService` does not implement the Windows whisper-mode capture behavior.

Still missing on Linux:

- runtime use of `WhisperModeEnabled`
- runtime use of profile `WhisperModeOverride`
- matching Windows audio-capture behavior when whisper mode is active

Impact:

- this remains dead configuration on Linux

### 3. Profile live-context tooling is still behind Windows

Windows exposes a richer profile authoring flow with live app/url detection helpers. Linux now has the core editor and runtime matching, but not the live-context conveniences.

Still missing on Linux:

- current focused app/url context panel
- "add current app" and "add current URL" helpers
- live matched-profile display in the Profiles section
- the Windows-style profile context window

Impact:

- Linux profiles work, but the authoring experience is still weaker than Windows

### 4. Dictation presentation still differs from the Windows overlay model

Windows has a dedicated floating overlay-style dictation presentation. Linux currently uses the main shell and section viewmodels for status.

Still missing on Linux:

- overlay presentation model parity
- active-profile/status overlay behavior
- closer visual and interaction parity during recording/processing

Impact:

- Linux runtime behavior is hooked up, but it does not yet look and feel the same while dictating

### 5. Partial/live transcript event parity is still incomplete

Linux now publishes the main lifecycle events, but it still does not match Windows for partial transcript / streaming-style updates.

Still missing on Linux:

- `PartialTranscriptionUpdateEvent`
- any streaming transcript state comparable to the Windows flow

Impact:

- plugins that depend on full live-update behavior may still have reduced functionality on Linux

## Updated Execution Strategy

The foundational hookup work is complete. Remaining work should now be done in parity order: user-visible Windows behavior first, then optional deeper UX polish.

### Phase 8. Port the prompt palette workflow

Primary goal:

- make Linux prompt actions usable the same way as Windows, not only through profile-bound dictation

Likely work:

- add Linux prompt-palette hotkey support
- add a Linux prompt palette UI
- route selected/captured text through prompt actions outside dictation

Acceptance criteria:

- Linux can trigger prompt actions without starting a dictation session
- behavior is close to the Windows prompt palette flow

### Phase 9. Implement whisper mode on Linux

Primary goal:

- make the shared whisper-mode setting and profile override affect Linux recording

Likely work:

- define the Linux whisper-mode capture behavior
- wire global and profile overrides into recording
- verify parity against the Windows capture path

Acceptance criteria:

- enabling whisper mode changes Linux recording behavior in a meaningful, testable way
- profile whisper overrides are no longer dead fields

### Phase 10. Improve profile authoring parity

Primary goal:

- bring the Linux Profiles section closer to the Windows workflow

Likely work:

- add live current-app/current-URL inspection
- add quick-add helpers for active process and URL
- surface the currently matched profile in the UI

Acceptance criteria:

- Linux users can create and validate profiles with less manual trial and error
- the Linux profiles workflow feels much closer to Windows

### Phase 11. Port overlay-style dictation presentation

Primary goal:

- make Linux dictation look and feel closer to the Windows recording experience

Likely work:

- design a Linux overlay shell/presentation model
- expose recording, processing, feedback, and active-profile state in that surface
- align timing and feedback behavior with Windows where practical

Acceptance criteria:

- Linux dictation provides a dedicated in-session presentation layer instead of only main-window status updates

### Phase 12. Add partial transcript / live update parity

Primary goal:

- close the remaining plugin/runtime event gap for streaming updates

Likely work:

- define Linux partial transcript production
- publish `PartialTranscriptionUpdateEvent`
- connect any needed UI/plugin consumers

Acceptance criteria:

- Linux plugins can receive incremental transcript updates where supported

## Suggested Issue Breakdown

These are the remaining GitHub issues implied by the current state:

1. Add Linux prompt palette hotkey and prompt palette UI
2. Implement Linux whisper mode and profile whisper overrides
3. Port Windows live profile-context helpers to Linux
4. Design and implement Linux overlay-style dictation presentation
5. Publish Linux partial transcription update events

## Verification

### Current baseline

These commands currently pass after the completed hookup work:

```bash
dotnet test tests/TypeWhisper.Linux.Tests/TypeWhisper.Linux.Tests.csproj -nologo
dotnet build TypeWhisper.slnx -nologo
```

### Remaining focused validation

1. Verify Linux prompt palette behavior matches Windows once that workflow is ported.
2. Verify whisper mode changes Linux capture behavior and that profile overrides take effect.
3. Verify current-app/current-URL helpers create working profiles without manual entry.
4. Verify overlay/session feedback on Linux matches the Windows recording lifecycle closely.
5. Verify partial transcript events are emitted and consumed correctly by Linux plugins.

## Recommendation

The next highest-value work is `prompt palette`, then `whisper mode`, then `profile live-context tooling`. Those are the most visible remaining places where the Windows app still does more than the Linux port.
