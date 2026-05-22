# Changelog

All notable changes to TypeWhisper (Linux) are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

TypeWhisper (Linux) is a Linux port of TypeWhisper, forked from the Windows
edition (`typewhisper-win`), which maintains its own independent `v0.x` release
line. This fork's versioning restarts at `0.x` and is unrelated to upstream's
version numbers; release tags use a `linux-v*` prefix to avoid colliding with
the upstream tags present in this repository. The `0.x` series covers pre-1.0
development; `1.0.0` is reserved for the first release verified across Wayland
compositors and used beyond the original author.

## [Unreleased]

_Phase 4 — Wayland support (in progress; targets 0.4.0)._

### Added

- Wayland global hotkeys via an `evdev` backend, with the shortcut backend
  abstracted behind `IGlobalShortcutBackend`.
- Wayland text insertion via `ydotool`, including a setup/diagnostics UI
  (experimental; not yet verified on Hyprland and other compositors).
- Wayland active-window and URL detection.
- Shortcuts settings panel with per-desktop shortcut writers, auto-setup, and a
  desktop-portal backend (stub).
- Full command-line interface — single-instance control over a Unix socket,
  subcommands, and a JSON control protocol.
- Settings backup and restore.
- Linux plugin data directory and per-plugin collection settings.

### Changed

- Atomic configuration writes and improved application logging.
- Hardened IPC / control-socket path security and directory permissions.

### Fixed

- Memory leaks in plugins, core services, and event-handler wiring.
- WebSocket close-handshake handling in the AssemblyAI streaming session.
- Thread safety in the local Gemma plugin.
- Configuration-loading error handling and `ydotool` process cleanup.
- A toggle-gate race that blocked starting a new dictation immediately after the
  previous one finished.
- Unhandled `OperationCanceledException` in the GNOME Shell active-window
  provider.

### Removed

- Windows-specific UI and services that no longer apply to the Linux build.
- Outdated Wayland global-shortcut design documents.

## [0.3.0] - 2026-05-07

_Phase 3 — Extended functionality beyond the Windows edition._

### Added

- Batch file transcription and a watch folder that transcribes new files
  automatically.
- Text-to-speech provider integration with Linux system-voice support.
- Recent transcriptions with customizable hotkeys for quick re-insertion.
- ElevenLabs plugin for cloud transcription with real-time streaming.
- CLI installation service that installs a `typewhisper` command, with usage
  examples surfaced in General settings.
- Translation and dictionary-management endpoints in the HTTP API.
- Prompt support for transcription plugins.
- Cancel an in-progress dictation with the `Escape` key.
- Auto-paste and clipboard management in the text-insertion service.
- Experimental Wayland text insertion via `wtype`.
- Project governance: issue/PR templates, a security policy, and a plugin
  smoke-test CI workflow.

### Changed

- Expanded the README to cover the new transcription, profile, and
  voice-command features.
- Improved error handling and state management in the ElevenLabs and Groq
  plugins.

### Fixed

- HTTP client timeout and error handling.
- Linux process execution under concurrent errors and timeouts.
- Word-count calculation in transcription records.
- Active-window detection and dictation handling on Linux.

## [0.2.0] - 2026-04-23

_Phase 2 — Full-featured X11 desktop application._

### Added

- Prompt palette: a hotkey-triggered overlay for running prompt actions against
  dictated or selected text.
- Live partial transcripts that stream into the UI while you speak.
- On-screen dictation overlay matching the Windows edition.
- File transcription and Recorder sections (replacing the earlier Audio and
  Models sections).
- HTTP API for controlling TypeWhisper and querying its state.
- Selectable compute backend (CPU or CUDA) for local transcription.
- Vocabulary boosting, term packs, and snippet tag filtering.
- Sound feedback, silence-based auto-stop, and system-command availability
  checks.
- Whisper mode and live profile switching in the dictation UI.
- Advanced and Appearance settings sections; reworked About and Prompts pages.
- Plugin model deletion, plus a plugin registry with session-audio management
  and playback.
- Speech-feedback and memory services.
- Linux install / uninstall scripts and a Linux-focused README.

### Changed

- Adopted FluentIcons for sidebar navigation.
- Refactored the audio recording service and Hybrid hotkey behavior.

### Fixed

- Hotkey collision and conflict handling.
- Plugin activation / deactivation, including activation concurrency.
- Dictation error handling and HTTP CORS handling.

### Security

- Hardening across the Linux platform layer and plugin system.

## [0.1.0] - 2026-04-21

_Phase 1 — Linux port (conversion from the Windows / WPF edition)._

### Added

- Initial Linux port of TypeWhisper, built on Avalonia in place of the
  Windows-only WPF UI.
- End-to-end dictation loop: audio capture → transcription → text insertion at
  the cursor.
- Cross-platform plugin SDK (decoupled from WPF), with a Linux plugin loader,
  plugin manager, and model manager.
- Application shell with sidebar navigation, settings sections, and a first-run
  onboarding wizard.
- System-tray integration with minimize-to-tray and a `CloseToTray` preference.
- Push-to-Talk and Hybrid hotkey modes alongside toggle dictation.
- Audio ducking and media-pause services that quiet other audio while
  dictating.
- LLM provider support and prompt actions for post-processing transcripts.
- Translation service with a UI for choosing a target language.
- History, dictionary, and snippet application so dashboard statistics
  populate.
- Plugin settings-provider abstraction with generated settings UI, plugin
  categorization, and async loading.

### Fixed

- Plugin discovery no longer loads duplicate SDK assemblies.
- Application exit no longer hangs; teardown is ordered and timeout-guarded.
- Resolved a blank-audio race in Push-to-Talk and Hybrid modes.

[Unreleased]: https://github.com/csmashe/typewhisper-linux/compare/linux-v0.3.0...linux-wayland
[0.3.0]: https://github.com/csmashe/typewhisper-linux/compare/linux-v0.2.0...linux-v0.3.0
[0.2.0]: https://github.com/csmashe/typewhisper-linux/compare/linux-v0.1.0...linux-v0.2.0
[0.1.0]: https://github.com/csmashe/typewhisper-linux/releases/tag/linux-v0.1.0
