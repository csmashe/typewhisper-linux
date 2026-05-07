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
- Drag-and-drop file transcription with batch queues, watch folders, and `ffmpeg`-based import when available
- Session recording to WAV with optional transcript sidecar text files
- Searchable history, recent transcriptions, dictionary corrections and term packs, snippets, and profiles
- Overlay positioning and left/right content widgets
- Tray integration and XDG autostart support
- Local HTTP API and installable CLI for desktop automation
- A user-level installer script that creates a desktop launcher and app icon

## Linux Branch Additions

This branch contains Linux-specific work that is not part of the original branch or the Windows branch:

- CUDA GPU support for the bundled whisper.cpp transcription engine on compatible NVIDIA systems
- Linux desktop integration through Avalonia, XDG autostart, Linux tray behavior, and a user-level desktop launcher
- Linux-specific checks that disable unavailable controls and explain missing tools such as `pactl`, `playerctl`, `canberra-gtk-play`, or CUDA runtime libraries
- Linux-focused plugin deployment so bundled plugins are copied into the user plugin directory on first run
- Linux session audio handling for dictation, file transcription, and recorder workflows
- Optional transcription cleanup pipeline with `Light` (deterministic), `Medium`, and `High` levels — Medium/High route through the configured LLM provider and degrade to Light when no provider is available
- Profile style presets — `Raw`, `Clean`, `Concise`, `Formal Email`, `Casual Message`, `Developer`, `Terminal Safe`, and `Meeting Notes` — that bundle cleanup level and formatting choices per profile, with optional cleanup and developer-formatting overrides
- Developer-safe formatting that converts spoken punctuation and casing commands (for example "dash dash", "open paren", "snake case") into code-friendly output
- Voice command suffixes parsed at the end of a dictation: `press enter`, `new paragraph`, `new line`, and `cancel`
- Transform-selection hotkey that voice-edits the text currently selected in another application
- Spoken IDE file references such as "at file dot ts" mapped to file tags for editor/IDE workflows
- Per-app text-insertion strategies (`Auto`, `Clipboard Paste`, `Direct Typing`, `Copy Only`) keyed by process name, with auto-paste retry and clipboard preservation
- Smart `Auto` insertion in browsers: types directly into web inputs, but falls back to clipboard paste when the active tab looks like a webmail composer
- Active-window detection covers Chromium, Firefox, and Firefox-derived browsers including Zen Browser, with title-based inference when process metadata is unavailable
- Correction suggestions generated from user edits in history, with optional auto-learning into the dictionary and confidence scoring
- Dictionary entries gain starring, priority, source tracking (`Manual`, `Import`, `CorrectionSuggestion`, `AutoLearned`), and times-applied/times-corrected stats
- Snippets gain an `Exact Phrase` trigger mode alongside `Anywhere`, plus per-profile scoping by profile id
- Dashboard insights panel summarizing average words and duration per dictation, insertion reliability, and top apps
- Spoken-feedback provider and voice selection on top of the existing toggle
- Aggressive short-clip transcription option for short, quiet utterances that would otherwise be discarded as silence
- Short-speech policy with peak-level and duration thresholds so accidental taps and silent clips are dropped before they reach the engine

## Features

### Transcription

- Plugin-based transcription engines for local and cloud workflows
- File transcription page for importing and transcribing audio files
- Batch file transcription queue with per-file status tracking
- Watch folders for automatic file transcription with selectable export format (`md`, `txt`, `srt`, `vtt`), optional language override, auto-start on app launch, and an optional delete-source-after-export step
- Subtitle export to SRT and WebVTT from the File transcription page when the active engine returns segment timing
- Recorder page for saving longer WAV captures and transcribing them after recording stops
- Dictation pipeline with post-processing through dictionary corrections and vocabulary boosting
- Bundled Linux plugins deployed on build and auto-copied into the user plugin directory on first run

### Dictation

- One main global dictation hotkey
- Activation modes: `Toggle` (press to start, press to stop), `Push to talk` (hold to record), and `Hybrid` (starts on press; a short tap keeps recording, holding past ~600 ms stops on release)
- Optional prompt palette hotkey
- Recent transcriptions palette and copy-last-transcription hotkey
- Cancel-in-flight via the `Escape` key during recording, transcription, or post-processing — only active while a dictation is running so it does not shadow modal dialogs or editors
- Auto-paste after transcription
- Whisper mode, silence auto-stop, sound feedback, audio ducking, and media pause settings in the Linux UI
- Live microphone preview and recording overlay

Some Linux dictation features depend on external desktop tools:

- Sound feedback uses `canberra-gtk-play`
- Audio ducking uses `pactl`
- Media pause uses `playerctl`
- Clipboard-backed auto-paste uses `xclip` (X11), `wl-copy`/`wl-paste` (Wayland), and a typing/paste backend selected per session — `wtype` is preferred on Wayland with a fallback to `xdotool` (X11 and XWayland apps)

When one of those tools is missing, the Linux UI disables that control and shows the reason, including session-aware install hints (for example, suggesting `wtype` on a Wayland session).

### Personalization

- Dictionary entries for corrections and terms
- Built-in term packs with enable/disable toggles
- Snippets with placeholder support such as `{date}`, `{time}`, `{datetime}`, `{clipboard}`, `{day}`, and `{year}`
- Profiles with rule matching, per-profile overrides, enable/disable state, and priority
- Prompt actions for LLM-driven text processing, provider overrides, and action plugin routing
- Optional long-term memory: when an `IMemoryStoragePlugin` (for example `FileMemory` or `OpenAiVectorMemory`) is enabled alongside a configured LLM provider, eligible transcriptions are sent to the LLM to extract durable facts that future prompt actions can recall as context

### Advanced Settings

The Advanced page exposes:

- History retention mode — `Duration` (default 90 days), `Forever`, or `Until app closes`
- `Save to history` toggle for runs you do not want stored
- Model auto-unload after a configurable idle timeout (`0` disables the auto-unload)
- Memory enable toggle, gated on having both a memory storage plugin and an available LLM provider
- Spoken feedback toggle, provider selection (defaults to the bundled Linux system TTS), and voice selection per provider

### Desktop Integration

- Tray icon support where the current desktop environment exposes a compatible system tray
- XDG autostart integration through `~/.config/autostart/typewhisper.desktop`
- Single-instance lock using `XDG_RUNTIME_DIR`
- Set `TYPEWHISPER_DISABLE_IME=1` to disable Avalonia X11 IME integration when debugging input-method issues
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
  - `xclip` (X11 clipboard) and `wl-copy`/`wl-paste` (Wayland clipboard) for clipboard-backed auto-paste
  - `wtype` for Wayland keyboard input, with `xdotool` as a fallback on X11 and XWayland apps
- Optional CUDA backend:
  - NVIDIA GPU and driver
  - CUDA 12 runtime/toolkit libraries providing `libcudart.so.12` and `libcublas.so.12`
  - CUDA currently applies to the bundled whisper.cpp engine; other bundled local engines stay on CPU

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

Model state on the Dictation page reports `Ready`, `Loading`, `Downloading <percent>`, or `Error`. Plugins that report `SupportsModelDownload` will trigger a download from the Dictation page when a not-yet-downloaded model is selected, and the API server can pause for that download via `?await_download=1`.

Known model gaps:

- Marketplace-style model browsing/management is not wired up in the Linux UI yet — model selection comes from whichever bundled or manually installed plugins are present and enabled.
- Local model availability depends on the Linux-compatible plugin implementation and any files it requires under the user data directory.

## HTTP API

The Linux app includes a local HTTP API for integrations and automation. Configure it in the General page:

- `Enable local API`
- `Port`, defaulting to `9876`
- Optional bearer token, when configured

When enabled, TypeWhisper listens on `http://localhost:<port>/`.

Available endpoints:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/status` | GET | App status and active model |
| `/v1/models` | GET | List available models |
| `/v1/transcribe` | POST | Transcribe uploaded audio (see options below) |
| `/v1/history` | GET | Search history |
| `/v1/history` | DELETE | Delete history entries |
| `/v1/profiles` | GET | List profiles |
| `/v1/profiles/toggle` | PUT | Toggle a profile on or off |
| `/v1/dictionary/terms` | GET | List dictionary terms |
| `/v1/dictionary/terms` | PUT | Add or update dictionary terms |
| `/v1/dictionary/terms` | DELETE | Delete dictionary terms |
| `/v1/dictation/start` | POST | Start recording |
| `/v1/dictation/stop` | POST | Stop recording |
| `/v1/dictation/status` | GET | Check dictation state |

`/v1/transcribe` accepts these optional form/query fields: `filename`, `language`, `language_hint` (repeatable), `task` (`transcribe` or `translate`), `target_language`, `model`, `engine`, `prompt`, and `response_format` (`json` or `verbose_json`). Append `?await_download=1` to wait while the active engine restores or downloads its model before transcribing.

When `response_format=verbose_json`, the response includes per-segment timing (`start`, `end`, `text`) alongside the standard `text`, `language`, `duration`, `noSpeechProbability`, `engine`, and `model` fields, so callers can build SRT/VTT output themselves if they need it.

Current API limitations:

- Uploaded audio conversion uses the same `ffmpeg`-based importer as the File transcription page.
- The API binds to `localhost` only.

## CLI

The Linux build ships a `typewhisper` CLI client that talks to the local API. Install it from the General page or by running `typewhisper`'s installer logic; it lands in `~/.local/bin/typewhisper`.

Commands:

- `typewhisper status` — show app status and active model
- `typewhisper models` — list available models
- `typewhisper transcribe <file|->` — transcribe an audio file (use `-` to read WAV bytes from stdin)

Useful options for `transcribe`: `--language`, `--language-hint` (repeatable), `--task transcribe|translate`, `--translate-to <code>`, `--response-format json|verbose_json`, `--prompt`, `--engine <id>`, `--model <id>`, `--await-download`.

Global options: `--port <N>` (defaults to `9876`), `--token <token>` or the `TYPEWHISPER_API_TOKEN` environment variable, `--json`, `--version`, and `--help`.

Examples:

```bash
typewhisper status --token "$TYPEWHISPER_API_TOKEN"
typewhisper transcribe recording.wav --language de --json
typewhisper transcribe recording.wav --engine groq --model whisper-large-v3-turbo
typewhisper transcribe - < audio.wav
```

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
│   └── TypeWhisper.Cli/         # CLI client for talking to the local API
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

- Transcription engines — bundled examples include `WhisperCpp`, `SherpaOnnx`, `GraniteSpeech`, `Qwen3Stt`, `Voxtral`, plus cloud engines `OpenAi`, `OpenAiCompatible`, `Groq`, `Deepgram`, `AssemblyAi`, `ElevenLabs`, `Speechmatics`, `Soniox`, `Gladia`, `CloudflareAsr`, and `GoogleCloudStt`
- LLM providers — `Claude`, `OpenAi`, `OpenAiCompatible`, `OpenRouter`, `Gemini`, `GemmaLocal`, `Groq`, `Cerebras`, `Cohere`, and `Fireworks`
- Action plugins — `Linear` and `Obsidian`
- Post-processing plugins — `Script` (run a shell command against the transcription)
- Memory storage plugins — `FileMemory` (local JSON) and `OpenAiVectorMemory` (embedding-backed recall)
- Companion plugins — `LiveTranscript` window and `Webhook` notifications

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
| `IMemoryStoragePlugin` | Persist and recall extracted memory entries |
| `ITtsProviderPlugin` | Add spoken-feedback voice providers |
| `ITypeWhisperPlugin` | Observe app/plugin events |

The SDK also includes helper types for plugin manifests, plugin events, transcription results, LLM requests, and action contexts.

Plugin source projects live under `plugins/`. The Linux app expects each deployed plugin to include its manifest and runtime assemblies in its plugin folder.

## Known Linux Gaps and Planned Work

These items appeared in the earlier project README or settings surface, but they are not fully implemented in this Linux branch yet and should be treated as planned work:

- Interface language switching is not implemented yet. The setting is visible, but the Linux UI does not currently live-switch translations.
- App self-update is not configured yet. The `Check for Updates` button in the About page is currently a placeholder.
- Marketplace/store browsing is intentionally not active in the Linux UI right now.
- Windows release channels and Velopack update-channel controls are not used by this Linux branch.
- The old README described broader platform feature coverage than this branch currently ships. Any feature not described as active above should be treated as pending until it is implemented in this repository.

## Development Notes

- Release builds run `scripts/deploy-linux-plugins.sh` to publish and bundle the Linux-capable plugins.
- On startup, the app deploys bundled plugins into the user plugin directory if they are missing.
- Session audio files are cleaned up on startup and shutdown so history retention preserves text without indefinitely retaining WAV captures.

## License

GPLv3 — see [LICENSE](LICENSE) for details. Commercial licensing available — see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). Trademark policy — see [TRADEMARK.md](TRADEMARK.md).
