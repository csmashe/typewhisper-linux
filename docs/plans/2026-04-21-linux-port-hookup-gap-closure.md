# Linux Port Hookup Gap Closure

## Context

This repository is a Linux port of the Windows TypeWhisper app. The current Linux app builds and runs, but several features that exist in the Windows product surface are only partially ported: some are present as UI and persisted settings, some are represented in models and services, and some are loaded through the plugin system, but they are not connected to the Linux runtime flow.

The goal of this plan is to close the highest-value wiring gaps in the Linux port without trying to reach immediate feature parity everywhere. The focus is on runtime hookups first: the parts that make existing UI, models, and plugins actually affect dictation behavior.

## Current Gaps

### 1. Plugin event bus is never published to from Linux runtime

The Linux port activates plugins and exposes `PluginEventBus`, but the app does not publish runtime events such as:

- `RecordingStartedEvent`
- `RecordingStoppedEvent`
- `TranscriptionCompletedEvent`
- `TranscriptionFailedEvent`
- `TextInsertedEvent`
- `PartialTranscriptionUpdateEvent`
- `ActionCompletedEvent`

Impact:

- Event-driven plugins like `LiveTranscript` and `Webhook` can load and activate, but they do not receive runtime data.
- The plugin system appears functional in settings while being disconnected from the actual dictation flow.

### 2. Profiles are stored and editable, but not applied during dictation

The Linux port includes:

- `ProfileService.MatchProfile(processName, url)`
- profile UI
- profile model fields for language, task, model override, prompt action, whisper mode, URL patterns, and hotkeys

However, the dictation runtime does not:

- resolve the active profile before transcription
- apply profile overrides to the effective transcription request
- persist `ProfileName` into history/events
- expose active profile state in the Linux UI

Impact:

- Profiles are mostly a data-entry screen, not a functional runtime feature.
- Several profile fields are currently dead configuration.

### 3. Prompt actions, post-processors, and LLM pipeline are not invoked

The Linux port registers:

- `IPromptActionService`
- `IPostProcessingPipeline`
- plugin manager indices for `ILlmProviderPlugin`, `IPostProcessorPlugin`, and `IActionPlugin`

But the Linux dictation flow currently performs:

- transcription
- dictionary corrections
- snippet expansion
- insertion
- history write

It does not invoke:

- `PostProcessingPipeline.ProcessAsync(...)`
- LLM provider execution for prompt actions
- plugin post-processors
- translation hooks

Impact:

- the Prompts section is CRUD-only
- LLM provider plugins are configurable but not used from Linux dictation
- post-processor plugins can load but do not affect text

### 4. Action plugins are loaded but never executed

The Linux plugin system exposes `IActionPlugin`, but the app does not define when or how action plugins should run during Linux dictation.

Impact:

- action plugins like Linear, Obsidian, and Script can be enabled but are operationally disconnected
- there is no Linux action execution policy or result reporting path

### 5. Dedicated Settings window exists but is unreachable

The Linux project includes:

- `SettingsWindow`
- `SettingsWindowViewModel`
- tabbed settings XAML

But the current app shell routes tray settings requests to the main window instead of opening the dedicated settings window.

Impact:

- dead code path and duplicate settings surfaces
- uncertainty about intended shell UX

### 6. Startup/autostart service exists but is not exposed

`StartupService` can create and remove an XDG autostart entry, but it is not registered or surfaced in the Linux UI.

Impact:

- useful Linux-native feature is present in code but unreachable

### 7. Audio ducking and media pause are settings-only

The Linux audio section persists:

- `AudioDuckingEnabled`
- `PauseMediaDuringRecording`

But DI explicitly notes the underlying Linux implementations are deferred.

Impact:

- visible UI suggests runtime behavior that does not exist yet
- settings are stored without effect

## Execution Strategy

The work should be done in dependency order. The plugin/event and profile/post-processing work are foundational; UI cleanup and smaller platform features should come after the runtime path is reliable.

### Phase 1. Wire runtime events and context into dictation

Primary goal:

- make the Linux dictation loop publish plugin-visible lifecycle events and carry enough context for downstream features

Files likely involved:

- `src/TypeWhisper.Linux/Services/DictationOrchestrator.cs`
- `src/TypeWhisper.Linux/Services/Plugins/PluginManager.cs`
- `src/TypeWhisper.Linux/Services/ActiveWindowService.cs`
- `src/TypeWhisper.PluginSDK/Models/PluginEvents.cs`

Required changes:

- inject or otherwise access the plugin event bus from `DictationOrchestrator`
- publish `RecordingStartedEvent` on recording start
- publish `RecordingStoppedEvent` when capture stops
- publish `TranscriptionCompletedEvent` on successful end-to-end transcription
- publish `TranscriptionFailedEvent` on transcription failures
- publish `TextInsertedEvent` after successful insertion or clipboard fallback
- ensure event payloads include app/process metadata, model/engine, duration, and later profile data

Acceptance criteria:

- `Webhook` plugin receives transcription completion events on Linux
- `LiveTranscript` receives at least start/stop/completed events on Linux
- failure path emits plugin-visible failure events

Notes:

- partial transcript events should be deferred unless streaming/polling partials are first-class in Linux
- do not invent new event shapes unless the existing SDK events are insufficient

### Phase 2. Resolve and apply profiles during dictation

Primary goal:

- make profiles influence actual dictation behavior

Files likely involved:

- `src/TypeWhisper.Linux/Services/DictationOrchestrator.cs`
- `src/TypeWhisper.Core/Services/ProfileService.cs`
- `src/TypeWhisper.Linux/ViewModels/Sections/ProfilesSectionViewModel.cs`
- `src/TypeWhisper.Linux/Views/Sections/ProfilesSection.axaml`
- possibly `src/TypeWhisper.Linux/ViewModels/Sections/DictationSectionViewModel.cs`

Required changes:

- resolve the active process name and browser URL before transcription
- call `MatchProfile(processName, url)`
- define an effective dictation context:
  - source language
  - selected task
  - model override
  - prompt action
  - whisper mode override
- persist active profile name into history and plugin events
- update Linux UI so the profiles screen can edit more than name + process list

Initial Linux scope recommendation:

- process-name matching
- URL matching where available
- language override
- model override
- prompt action binding
- profile name in history/events

Deferred unless needed immediately:

- per-profile hotkeys
- full website-aware behavior on Wayland
- whisper mode override if audio stack support is incomplete

Acceptance criteria:

- a matching profile changes effective language/model/prompt selection during dictation
- the selected profile name is visible in saved history records
- plugin events include `ProfileName`

### Phase 3. Integrate the post-processing pipeline

Primary goal:

- replace the hand-written dictation post-processing path with the existing pipeline abstraction

Files likely involved:

- `src/TypeWhisper.Linux/Services/DictationOrchestrator.cs`
- `src/TypeWhisper.Core/Interfaces/IPostProcessingPipeline.cs`
- `src/TypeWhisper.Core/Services/PostProcessingPipeline.cs`
- `src/TypeWhisper.Linux/Services/Plugins/PluginManager.cs`

Required changes:

- inject `IPostProcessingPipeline` into the Linux dictation flow
- build `PipelineOptions` using:
  - dictionary corrector
  - snippet expander
  - vocabulary booster if applicable
  - plugin post-processors from active plugins
  - status callback
- preserve current behavior where pipeline failures should not break transcription output

Acceptance criteria:

- dictionary and snippets still work after migration
- enabled post-processor plugins affect final text on Linux
- pipeline failures degrade gracefully and keep dictation usable

### Phase 4. Hook prompt actions and LLM providers into the pipeline

Primary goal:

- make the Prompts section operational instead of storage-only

Files likely involved:

- `src/TypeWhisper.Linux/Services/DictationOrchestrator.cs`
- `src/TypeWhisper.Linux/Services/Plugins/PluginManager.cs`
- `src/TypeWhisper.Core/Services/PromptActionService.cs`
- provider plugins that expose `ILlmProviderPlugin`

Required changes:

- choose a Linux rule for selecting the active prompt action:
  - profile-bound prompt action, or
  - a globally enabled/default prompt action
- choose a Linux rule for selecting the active LLM provider/model
- add `LlmHandler` to pipeline options
- surface useful user-facing status during AI processing

Open design question:

- whether Linux should support exactly one active prompt action in dictation, or a user-selected prompt palette workflow later

Recommended first implementation:

- support one optional prompt action in dictation
- source it from the active profile first
- if no profile prompt action is set, do not run LLM processing automatically

Acceptance criteria:

- a profile-configured prompt action runs through an enabled LLM provider on Linux
- processed text is inserted and saved as final text
- failures fall back to the pre-LLM text with a clear status message

### Phase 5. Define and implement action plugin execution

Primary goal:

- decide when action plugins run and wire that into Linux dictation

Files likely involved:

- `src/TypeWhisper.Linux/Services/DictationOrchestrator.cs`
- `src/TypeWhisper.PluginSDK/Models/ActionContext.cs`
- `src/TypeWhisper.PluginSDK/IActionPlugin.cs`

Design options:

1. Auto-run all enabled action plugins after successful transcription.
2. Allow actions to be bound per profile.
3. Add an explicit user-triggered execution surface later and do not auto-run yet.

Recommended first implementation:

- do not auto-run all action plugins globally
- support action execution only when explicitly bound by profile or future UI

Reason:

- action plugins can have side effects like issue creation or file writes
- global auto-execution is too risky as a Linux default

Acceptance criteria:

- at least one explicit binding path exists for Linux action execution
- action completion emits `ActionCompletedEvent`
- action failures are surfaced without breaking base dictation

### Phase 6. Clean up shell/UI gaps

Primary goal:

- remove dead shell paths or make them real

Subtasks:

- decide whether Linux keeps:
  - a single main-window settings shell, or
  - a separate `SettingsWindow`
- if separate settings window is kept:
  - wire tray settings to open it
  - decide whether main window remains dashboard-first
- if separate settings window is dropped:
  - remove `SettingsWindow` and `SettingsWindowViewModel`

- expose `StartupService` in General settings
- add a Linux autostart toggle

Acceptance criteria:

- there is exactly one intentional settings navigation model
- autostart can be toggled from Linux UI and persists correctly

### Phase 7. Audio routing implementation or UI gating

Primary goal:

- stop presenting non-functional controls as if they work

Options:

1. Implement Linux audio ducking and media pause.
2. Hide or disable those controls until implementation lands.

Recommended first implementation:

- disable or clearly badge the controls as unsupported in current Linux builds unless implementation is ready in the same workstream

Potential implementation targets:

- ducking via `pactl`
- media pause via MPRIS over D-Bus

Acceptance criteria:

- the UI no longer implies completed functionality when runtime support is absent

## Suggested Issue Breakdown

These can become GitHub issues after the plan is reviewed:

1. Publish Linux dictation lifecycle events to the plugin event bus
2. Apply active profile matching and overrides in Linux dictation
3. Replace ad hoc Linux text post-processing with `IPostProcessingPipeline`
4. Execute prompt actions through enabled Linux LLM provider plugins
5. Define and wire explicit action plugin execution on Linux
6. Resolve Linux settings shell duplication and wire tray settings intentionally
7. Expose XDG autostart through Linux General settings
8. Implement or gate Linux audio ducking and media pause controls

## Verification

### Build

```bash
dotnet build TypeWhisper.slnx -nologo
```

### Focused validation

1. Enable `Webhook` plugin and verify Linux dictation emits completion payloads.
2. Enable `LiveTranscript` and verify it reacts to Linux recording lifecycle events.
3. Create a profile with language/model/prompt overrides and verify dictation behavior changes in the matching app.
4. Configure an LLM provider plus one prompt action and verify Linux dictation runs it.
5. Save history and confirm profile name, engine, model, and final text are recorded correctly.
6. Verify tray settings behavior matches the chosen shell design.
7. Toggle autostart and confirm `~/.config/autostart/typewhisper.desktop` is created/removed.

## Recommendation

Start with Phases 1 through 3 in one implementation slice. They unlock the real Linux runtime path and make existing plugins, profile data, and post-processing abstractions meaningful. After that, Phases 4 and 5 can be implemented with less rework because the required context and lifecycle hooks will already exist.
