# TypeWhisper for Linux

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Linux](https://img.shields.io/badge/Linux-Desktop-FCC624.svg)](https://kernel.org)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Speech-to-text and AI text processing for Linux desktop. This repository is a Linux desktop port forked from the TypeWhisper project, which provides macOS and Windows versions. I ported it so I could use TypeWhisper on Linux, and I am making this branch available for other Linux users who want the same.

If the TypeWhisper project releases an official Linux version, or if this port is merged into the main TypeWhisper branch, I plan to use the upstream Linux version instead. Until then, this branch exists as a practical Linux port adapted around Avalonia, Linux desktop services, and Linux-friendly install and startup behavior.

TypeWhisper lets you dictate into other applications, transcribe audio files, record longer WAV sessions, apply dictionary and snippet post-processing, and run prompt-based AI text actions through plugins.

## Current Linux Scope

The Linux branch currently includes:

- Global dictation with toggle, push-to-talk, and hybrid activation modes
- A Linux desktop UI with dashboard, dictation, shortcuts, file transcription, recorder, history, dictionary, snippets, profiles, prompts, extensions, general, appearance, advanced, and about sections
- Plugin-backed transcription engines and prompt/LLM providers
- Drag-and-drop file transcription with `ffmpeg`-based import when available
- Session recording to WAV with optional transcript sidecar text files
- Searchable history, dictionary corrections and term packs, snippets, and profiles
- Overlay positioning and left/right content widgets
- Tray integration and XDG autostart support
- A user-level installer script that creates a desktop launcher and app icon

## Features

### Transcription

- Plugin-based transcription engines for local and cloud workflows
- File transcription page for importing and transcribing audio files
- Recorder page for saving longer WAV captures and transcribing them after recording stops
- Dictation pipeline with post-processing through dictionary corrections and vocabulary boosting
- Bundled Linux plugins deployed on build and auto-copied into the user plugin directory on first run

### Dictation

- One main global dictation hotkey
- Activation modes: `Toggle`, `Push to talk`, and `Hybrid`
- Optional prompt palette hotkey
- Auto-paste after transcription
- Whisper mode, silence auto-stop, sound feedback, audio ducking, and media pause settings in the Linux UI
- Live microphone preview and recording overlay

Some Linux dictation features depend on external desktop tools:

- Sound feedback uses `canberra-gtk-play`
- Audio ducking uses `pactl`
- Media pause uses `playerctl`

When one of those tools is missing, the Linux UI disables that control and shows the reason.

### Personalization

- Dictionary entries for corrections and terms
- Built-in term packs with enable/disable toggles
- Snippets with placeholder support such as `{date}`, `{time}`, `{datetime}`, `{clipboard}`, `{day}`, and `{year}`
- Profiles with rule matching, per-profile overrides, enable/disable state, and priority
- Prompt actions for LLM-driven text processing

### Desktop Integration

- Tray icon support where the current desktop environment exposes a compatible system tray
- XDG autostart integration through `~/.config/autostart/typewhisper.desktop`
- Single-instance lock using `XDG_RUNTIME_DIR`
- Desktop install script that publishes the app, installs it under the user profile, and creates a launcher icon

## Linux Requirements

- A modern Linux desktop session
- .NET 10 SDK to build from source
- `ffmpeg` for file transcription imports beyond already-supported direct formats
- Optional desktop helpers:
  - `pactl` for audio ducking
  - `playerctl` for media pause during recording
  - `canberra-gtk-play` for sound feedback
  - `espeak-ng`, `espeak`, or `spd-say` for spoken feedback

## Tested On

This Linux branch has only been tested on the maintainer's current setup so far:

- Pop!_OS 22.04 LTS
- GNOME 42.9
- X11 session

Linux desktop behavior can vary by distribution, compositor, desktop environment, and especially Wayland implementation. Wayland support paths are included where possible, but they may not behave the same across all setups.

If you run into a setup-specific issue, please create an issue or open a pull request with the distribution, desktop environment, display server, reproduction steps, and any relevant logs.

## Build and Run

1. Clone the repository:
   ```bash
   git clone https://github.com/TypeWhisper/typewhisper-linux.git
   cd typewhisper-linux
   ```

2. Build:
   ```bash
   dotnet build
   ```

3. Run from source:
   ```bash
   dotnet run --project src/TypeWhisper.Linux
   ```

## Install as a Desktop App

To install a clickable launcher with an icon for the current user:

```bash
./scripts/install-linux-app.sh
```

This script:

- publishes `src/TypeWhisper.Linux` as a self-contained Linux app
- bundles Linux plugins into the published output
- installs the app into `~/.local/share/TypeWhisper`
- creates `~/.local/share/applications/typewhisper.desktop`
- registers the application icon under the user icon theme

To remove that user-level install:

```bash
./scripts/uninstall-linux-app.sh
```

## Models

TypeWhisper uses plugin-provided transcription models. In this Linux branch, models appear in the Dictation page after the bundled or installed transcription plugins are loaded.

Current model behavior:

- The selected transcription model is saved in settings.
- The Dictation page can load the selected model through the active transcription plugin.
- File transcription and recorder transcription use the same selected model.
- Model auto-unload is exposed in Advanced settings.

Known model gaps:

- Model download and marketplace-style model management are not fully wired in the Linux UI yet.
- Available models depend on which bundled or manually installed plugins are present and enabled.
- Local model availability depends on the Linux-compatible plugin implementation and any files it requires under the user data directory.

## HTTP API

The Linux app includes a local HTTP API for integrations and automation. Configure it in the General page:

- `Enable local API`
- `Port`, defaulting to `9876`

When enabled, TypeWhisper listens on `http://localhost:<port>/`.

Available endpoints:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/status` | GET | App status and active model |
| `/v1/models` | GET | List available models |
| `/v1/transcribe` | POST | Transcribe uploaded audio. Optional query params: `filename`, `language`, `task`, `model` |
| `/v1/history` | GET | Search history |
| `/v1/history` | DELETE | Delete history entries |
| `/v1/profiles` | GET | List profiles |
| `/v1/profiles/toggle` | PUT | Toggle a profile on or off |
| `/v1/dictation/start` | POST | Start recording |
| `/v1/dictation/stop` | POST | Stop recording |
| `/v1/dictation/status` | GET | Check dictation state |

Current API limitations:

- `/v1/transcribe` returns text, language, duration, and no-speech probability when available. Segment-level subtitle timing is not exposed by the current Linux plugin result contract.
- Uploaded audio conversion uses the same `ffmpeg`-based importer as the File transcription page.
- The API binds to `localhost` only.

## Profiles

Profiles let TypeWhisper apply different settings based on the active application or URL pattern.

In the Linux branch, profiles support:

- Profile creation, editing, enable/disable, save, and delete
- Process/app matching fields
- URL pattern fields
- Priority
- Language, task, translation, model, whisper mode, and prompt action overrides
- A live-context view for checking what app context TypeWhisper sees

Example profile uses:

- Use a specific language for one editor or browser
- Enable whisper mode for a quiet-room workflow
- Use a different transcription model for one app
- Run a specific prompt action for text captured in a matching context

Known profile gaps:

- Active-window and URL detection can vary by desktop environment, browser, and display server.
- Some Wayland sessions may restrict app/window metadata more than X11.

## Project Layout

```text
typewhisper-linux/
├── src/
│   ├── TypeWhisper.Core/        # Shared core logic, data models, persistence, services
│   ├── TypeWhisper.PluginSDK/   # Plugin SDK for transcription, LLM, actions, and events
│   ├── TypeWhisper.Linux/       # Avalonia-based Linux desktop application
│   └── TypeWhisper.Cli/         # CLI client for talking to the local API when it is implemented
├── plugins/                     # Plugin source projects
├── scripts/                     # Linux build, deploy, and install scripts
├── docs/                        # Planning and release notes
└── tests/                       # Automated tests
```

## Data and Paths

TypeWhisper stores its Linux data under the user-local application data directory exposed by .NET:

- Base path: `~/.local/share/TypeWhisper` on typical Linux setups
- Settings: `settings.json`
- Database: `Data/typewhisper.db`
- Logs: `Logs/`
- Plugins: `Plugins/`
- Audio: `Audio/`
- Plugin data: `PluginData/`

## Plugins

The Linux app uses the shared plugin model from the TypeWhisper codebase. Plugin categories used by this branch include:

- Transcription engines
- LLM providers
- Action plugins
- Event and post-processing plugins

The Linux build currently deploys bundled plugins from `plugins/` into the app output, then copies them into the user plugin directory on first run if they are missing.

Plugins are loaded from the user plugin directory:

- `~/.local/share/TypeWhisper/Plugins/` on typical Linux setups

Bundled plugin deployment:

- Release builds run `scripts/deploy-linux-plugins.sh`.
- The install script also bundles plugins into the published app.
- On first run, bundled plugins are copied into the user plugin directory if they are missing.

Known plugin gaps:

- Marketplace/store browsing is intentionally not active in the Linux UI right now.
- Plugin update handling is limited compared with the intended full marketplace workflow.
- Some plugins may depend on external binaries, API keys, local model files, or services that must be configured separately.

## Plugin SDK

Plugin projects use `TypeWhisper.PluginSDK`.

The SDK defines the shared plugin contracts used by the Linux app:

| Interface | Purpose |
|-----------|---------|
| `ITranscriptionEnginePlugin` | Add a local, cloud, or custom transcription engine |
| `ILlmProviderPlugin` | Add an LLM provider for prompt processing |
| `IPostProcessorPlugin` | Add text cleanup or transformation steps after transcription |
| `IActionPlugin` | Run custom actions from transcriptions or prompt results |
| `ITypeWhisperPlugin` | Observe app/plugin events |

The SDK also includes helper types for plugin manifests, plugin events, transcription results, LLM requests, and action contexts.

Plugin source projects live under `plugins/`. The Linux app expects each deployed plugin to include its manifest and runtime assemblies in its plugin folder.

## Known Linux Gaps and Planned Work

These items appeared in the earlier project README or settings surface, but they are not fully implemented in this Linux branch yet and should be treated as planned work:

- Interface language switching is not implemented yet. The setting is visible, but the Linux UI does not currently live-switch translations.
- App self-update is not configured yet. The `Check for Updates` button in the About page is currently a placeholder.
- Marketplace/store browsing is intentionally not active in the Linux UI right now.
- Subtitle export formats such as SRT and WebVTT are not currently wired up in the Linux file transcription flow.
- The old README described broader platform feature coverage than this branch currently ships. Any feature not described as active above should be treated as pending until it is implemented in this repository.

## Development Notes

- Release builds run `scripts/deploy-linux-plugins.sh` to publish and bundle the Linux-capable plugins.
- On startup, the app deploys bundled plugins into the user plugin directory if they are missing.
- Session audio files are cleaned up on startup and shutdown so history retention preserves text without indefinitely retaining WAV captures.

## License

GPLv3 — see [LICENSE](LICENSE) for details. Commercial licensing available — see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). Trademark policy — see [TRADEMARK.md](TRADEMARK.md).
