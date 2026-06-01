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
- A Linux desktop UI with dashboard, dictation, shortcuts, text insertion, file transcription, recorder, history, dictionary, snippets, profiles, prompts, plugins, general, appearance, advanced, and about sections
- Plugin-backed transcription engines and prompt/LLM providers
- Drag-and-drop file transcription with batch queues, watch folders, and `ffmpeg`-based import when available
- Session recording to WAV with optional transcript sidecar text files
- Searchable history, recent transcriptions, dictionary corrections and term packs, snippets, and profiles
- Configurable dictation overlay with selectable Indicator, Waveform, and Text widgets, including a live audio-level waveform visualization
- Tray integration and XDG autostart support
- Settings backup and restore
- Local HTTP API and installable CLI for desktop automation
- A user-level installer script that creates a desktop launcher and app icon

## Linux Branch Additions

This branch contains Linux-specific work that is not part of the original branch or the Windows branch:

- CUDA GPU support for the bundled whisper.cpp transcription engine on compatible NVIDIA systems
- Linux desktop integration through Avalonia, XDG autostart, Linux tray behavior, and a user-level desktop launcher
- Wayland global hotkey detection via an evdev backend that reads `/dev/input/event*` directly, so the configured shortcut fires regardless of which window has focus; falls back to the XDG portal and then focused-only SharpHook when the evdev path is unavailable. Enabled by default, requires the current user to be in the `input` group, and can be turned off from Settings → Shortcuts to keep focused-only behavior
- A Shortcuts settings panel with per-desktop shortcut writers (GNOME, KDE, Hyprland, Sway) and a one-click auto-setup flow, so the configured TypeWhisper hotkey is registered with the active desktop environment without hand-editing config files
- Linux-specific checks that disable unavailable controls and explain missing tools such as `pactl`, `playerctl`, `canberra-gtk-play`, or CUDA runtime libraries
- Linux-focused plugin deployment so bundled plugins are copied into the user plugin directory on first run
- Linux session audio handling for dictation, file transcription, and recorder workflows
- Optional transcription cleanup pipeline with `Light` (deterministic), `Medium`, and `High` levels — Medium/High route through the configured LLM provider and degrade to Light when no provider is available
- Profile style presets — `Raw`, `Clean`, `Concise`, `Formal Email`, `Casual Message`, `Developer`, `Terminal Safe`, and `Meeting Notes` — that bundle cleanup level and formatting choices per profile, with optional cleanup and developer-formatting overrides
- Developer-safe formatting that converts spoken punctuation and casing commands (for example "dash dash", "open paren", "snake case") into code-friendly output
- Voice command suffixes parsed at the end of a dictation: `press enter`, `new paragraph`, `new line`, and `cancel`
- Spoken IDE file references such as "at file dot ts" mapped to file tags for editor/IDE workflows
- Per-app text-insertion strategies (`Auto`, `Clipboard Paste`, `Direct Typing`, `Copy Only`) keyed by process name, with auto-paste retry and clipboard preservation
- Smart `Auto` insertion that picks per-target: types directly into supported browsers (falling back to clipboard paste when the title looks like a webmail composer), types directly into terminals and the Codex CLI (where synthesized Ctrl+V isn't interpreted as paste), and on Wayland sessions where the focused app can't be identified, prefers direct typing for ASCII text and clipboard paste for non-ASCII text
- Extended active-window browser coverage to include the Zen Browser and LibreWolf on top of the Chromium/Firefox families the upstream Windows build already supports, with title-based inference when process metadata is unavailable
- Correction suggestions generated from user edits in history, with optional auto-learning into the dictionary and confidence scoring
- Dictionary entries gain starring, priority, source tracking (`Manual`, `Import`, `CorrectionSuggestion`, `AutoLearned`), and times-applied/times-corrected stats
- Snippets gain an `Exact Phrase` trigger mode alongside `Anywhere`, plus per-profile scoping by profile id
- Dashboard insertion-reliability metric and per-dictation averages (average words and duration) on top of the upstream dashboard's words-per-minute, top-apps, and time-saved tiles

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
- Transform-selection hotkey that voice-edits the text currently selected in another application
- Cancel-in-flight via the `Escape` key during recording, transcription, or post-processing — only active while a dictation is running so it does not shadow modal dialogs or editors
- Auto-paste after transcription
- Whisper mode, silence auto-stop, sound feedback, audio ducking, and media pause settings in the Linux UI
- Aggressive short-clip transcription option for short, quiet utterances that would otherwise be discarded as silence
- Short-speech policy with peak-level and duration thresholds so accidental taps and silent clips are dropped before they reach the engine
- Live microphone preview and recording overlay

Some Linux dictation features depend on external desktop tools:

- Sound feedback uses `canberra-gtk-play`
- Audio ducking uses `pactl`
- Media pause uses `playerctl`
- Clipboard-backed auto-paste uses `xclip` (X11), `wl-copy`/`wl-paste` (Wayland), and a typing/paste backend selected per session. On wlroots compositors (Hyprland, Sway) `wtype` is tried first; on GNOME and KDE Wayland — which omit the wtype virtual-keyboard protocol — `ydotool` is tried first instead, with `wtype` and `xdotool` as later fallbacks. X11 sessions use `xdotool`.

When one of those tools is missing, the Linux UI disables that control and shows the reason, including session-aware install hints (for example, suggesting `wtype` on a wlroots Wayland session, or `ydotool` on GNOME / KDE Wayland). The **Text insertion** settings panel surfaces the current backend chain and offers a one-click setup flow for the `ydotool` daemon and `input`-group membership when needed.

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

- Tray icon support where the current desktop environment exposes a compatible system tray; the "close to tray" setting is gated on whether a real system tray is actually registered (detected via a D-Bus probe at startup) so the app can't hide itself with no way back to the UI
- XDG autostart integration through `~/.config/autostart/typewhisper.desktop`
- Single-instance enforcement via a Unix control socket under `XDG_RUNTIME_DIR` (falling back to a `0700` directory under `/tmp` when `XDG_RUNTIME_DIR` is unavailable); a second launch hands its CLI command off to the already-running instance over a JSON control protocol instead of starting a new window
- Set `TYPEWHISPER_DISABLE_IME=1` to disable Avalonia X11 IME integration when debugging input-method issues
- Desktop install script that publishes the app, installs it under the user profile, and creates a launcher icon

#### GNOME Wayland tray icons

GNOME Shell does not show AppIndicator/KStatusNotifier tray icons by default.
On Fedora GNOME Wayland, install and enable the AppIndicator extension if you
want TypeWhisper's tray menu/icon in the top bar:

```bash
sudo dnf install -y gnome-shell-extension-appindicator
gnome-extensions enable appindicatorsupport@rgcjonas.gmail.com
```

If `gnome-extensions enable` reports that the extension does not exist right
after installation, log out and back in so GNOME Shell reloads system
extensions, then run the enable command again. Restart TypeWhisper after the
extension is loaded.

The tray icon is separate from the launcher/dock icon. When running from
source with `dotnet run`, GNOME may not match the process to a registered
desktop entry, so the dock or app switcher can show a generic icon. The
desktop installer registers the `.desktop` file and icon theme entry for that
case.

#### GNOME Wayland active-window detection

Profile matching by process name (e.g. matching on `firefox`, `code`,
`soffice.bin`) requires TypeWhisper to know which window has focus.
TypeWhisper picks a compositor-native provider per session — `xdotool` on
X11/XWayland, `hyprctl` on Hyprland, `swaymsg` on Sway, `kdotool` on KDE
Plasma — and falls back to a Linux process-name lookup via `/proc/PID/comm`
so user profiles built against X11 keep working unchanged on Wayland.

On GNOME Wayland there is no built-in way for an unprivileged app to ask
"what's the active window" — the built-in `org.gnome.Shell.Introspect`
D-Bus API returns `AccessDenied` for everyone except trusted clients. The
fix is the user-installed **Window Calls** GNOME Shell extension:

1. Install from <https://extensions.gnome.org/extension/4974/window-calls/>
   (the Profiles section in TypeWhisper has an **Install Window Calls
   extension** button that opens this page when the extension is missing).
2. Once enabled, restart TypeWhisper — no logout required. The extension's
   D-Bus interface (`org.gnome.Shell.Extensions.Windows`) is detected at
   the next snapshot tick.

Without the extension, GNOME Wayland users can still use URL-only profile
rules and any global (no-match) profile, but app-name matching will not
fire.

#### Wayland URL detection for browser-based profile rules

URL-based profile rules (`mail.google.com`, `*.github.com`, etc.) need
TypeWhisper to read the browser's address bar. On X11 the existing
`xdotool` + `xclip` Ctrl+L/Ctrl+C trick covers this without any browser
configuration. On Wayland synthetic-input shortcuts are blocked by the
compositor, so TypeWhisper falls back to walking the browser's
[AT-SPI](https://docs.gtk.org/atspi2/) accessibility tree — which only
works if the browser is exposing it.

The Profiles section in TypeWhisper has an **Enable browser URL
detection** button that:

- Writes `~/.config/environment.d/typewhisper-accessibility.conf` setting
  `MOZ_ENABLE_ACCESSIBILITY=1` and `GTK_MODULES=gail:atk-bridge` for
  Firefox-family browsers.
- Patches user-local `.desktop` launchers for Firefox / Zen / LibreWolf
  and Chromium / Chrome / Edge / Brave / Vivaldi / Opera so the
  appropriate flag (`MOZ_ENABLE_ACCESSIBILITY=1` for Firefox-family,
  `--force-renderer-accessibility` for Chromium-family) is set inline on
  every menu launch — independent of whether `systemd --user` reloaded
  the `environment.d` file across logouts.
- Backs up any non-owned user `.desktop` files in
  `~/.local/share/typewhisper/launcher-backups/` so the integration can
  be cleanly removed without losing user customizations.

**Firefox additionally needs the lazy-init gate flipped.** Modern Firefox
(100+) refuses to register on AT-SPI until either an assistive
technology connects or `accessibility.force_disabled` is explicitly set:

1. Open `about:config` in Firefox, accept the warning.
2. Search for `accessibility.force_disabled`.
3. Edit the value from `0` to `-1` (force-enable always).
4. Restart Firefox. Verify by visiting `about:support` — the
   **Accessibility** section should now say `Activated: Yes`.

After Firefox is on the AT-SPI bus, the TypeWhisper walker finds the
address-bar element automatically and surfaces the URL to the profile
matcher. The walker has a 1.2 s per-call budget and caches the matched
URL for 10 s, so transient title bumps (Gmail badge updates,
draft-saved overlays, etc.) do not force constant re-walking.

When URL detection fails, the Profiles section banner explains what's
missing, and the Error Log on the About page records a one-line
diagnostic per unique state. Look for entries like
`AT-SPI URL walk: process=firefox matched-app='Firefox' nodes-walked=N
best-score=... result=...` — `matched-app=none` means the browser
isn't exposing AT-SPI, `result=null` with a non-null `best-score` means
the walker reached the address bar but didn't recognise it.

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
  - `wtype` (wlroots Wayland: Hyprland, Sway) and `ydotool` (GNOME / KDE Wayland, where wtype is unavailable) for Wayland keyboard input; `xdotool` as a fallback on X11 and XWayland apps. `ydotool` requires its daemon to be running and the current user to be in the `input` group
- Optional CUDA backend:
  - NVIDIA GPU and driver
  - CUDA 12 runtime/toolkit libraries providing `libcudart.so.12` and `libcublas.so.12`
  - CUDA currently applies to the bundled whisper.cpp engine; other bundled local engines stay on CPU

## Tested On

This Linux branch has been tested on the maintainer's current setups:

- Pop!_OS 22.04 LTS / GNOME 42.9 / X11 session
- Fedora 44 / GNOME 46+ / Wayland session (with the Window Calls
  extension installed for active-window detection)

Other Wayland setups (Hyprland, Sway, KDE Plasma, and other GNOME
versions) should work via their respective compositor-native window
providers, but have not been tested at this time.

Linux desktop behavior can vary by distribution, compositor, desktop
environment, and especially Wayland implementation. Compositor-native
window providers exist for Hyprland, Sway, KDE Plasma (via `kdotool`),
and GNOME (via the Window Calls extension); URL detection on Wayland
uses AT-SPI and requires browser-side accessibility to be enabled — see
*Wayland URL detection for browser-based profile rules* above.

If you run into a setup-specific issue, please create an issue or open a
pull request with the distribution, desktop environment, display server,
reproduction steps, and any relevant logs (the Error Log section on the
About page has a per-window AT-SPI walk diagnostic for URL detection
issues).

## Download a Prebuilt Release

Tagged releases on [GitHub Releases](https://github.com/csmashe/typewhisper-linux/releases) ship four formats for `linux-x64`. Pick whichever fits your distribution and root preference:

| Format | Filename | Where it installs | Notes |
|--------|----------|-------------------|-------|
| AppImage | `TypeWhisper-<version>-x86_64.AppImage` | Anywhere — run the file directly | Portable, no install step. `chmod +x` and double-click or run from a terminal. |
| Debian / Ubuntu `.deb` | `typewhisper_<version>_amd64.deb` | `/opt/typewhisper` with `/usr/bin/typewhisper` wrapper | `sudo apt install ./typewhisper_<version>_amd64.deb`. Recommends `libpulse0`, `pulseaudio-utils`, `playerctl`, `xdotool`. |
| Fedora / RHEL `.rpm` | `typewhisper-<version>-1.x86_64.rpm` | `/opt/typewhisper` with `/usr/bin/typewhisper` wrapper | `sudo dnf install ./typewhisper-<version>-1.x86_64.rpm`. Recommends `pulseaudio-libs`, `pulseaudio-utils`, `playerctl`, `xdotool`. |
| Tarball | `typewhisper-linux-x64-<version>.tar.gz` | User-local: `~/.local/share/TypeWhisper` + `~/.local/bin/typewhisper` symlink | No root required. Extract, then `./install.sh` (or `./install.sh --uninstall` to remove). |

All four formats bundle the self-contained .NET runtime and the Linux plugins — `.NET 10 SDK` is only needed if you're building from source. Optional desktop helpers (`pactl`, `playerctl`, `wtype` / `ydotool` / `xdotool`, `wl-copy`/`xclip`, `canberra-gtk-play`, `espeak-ng`) are still installed via your distro; see *Linux Requirements* above.

The Wayland typing backend (`ydotool` on GNOME/KDE, `wtype` on wlroots) and Wayland global hotkeys (`input`-group membership for the evdev backend) still need their per-distro setup steps regardless of which package format you install.

## Build and Run

1. Clone the repository:
   ```bash
   git clone https://github.com/csmashe/typewhisper-linux.git
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

Profile-rule prerequisites on Wayland:

- App-name rules need a compositor-native window provider — installed by
  default on Hyprland (`hyprctl`) and Sway (`swaymsg`), available via
  `kdotool` on KDE Plasma, and via the user-installed **Window Calls**
  extension on GNOME. See *GNOME Wayland active-window detection* above.
- URL-pattern rules need browser-side AT-SPI accessibility enabled. The
  Profiles section has an **Enable browser URL detection** button that
  patches the relevant launchers and writes the env file; Firefox users
  additionally need the `about:config` flip described in *Wayland URL
  detection* above.
- The Profiles section shows live diagnostic banners when window detection
  fails repeatedly, with a one-click remediation button for the missing
  piece. The Error Log on the About page records per-state walk
  diagnostics that pinpoint exactly which step failed.

Known profile gaps:

- Active-window detection on KDE Plasma requires `kdotool` to be
  installed; without it, app-name matching is unavailable in that
  session.

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

- Transcription engines — bundled examples include `WhisperCpp` (with a configurable `noSpeechThreshold` for filtering silent segments to reduce hallucinated phrases), `SherpaOnnx`, `Qwen3Stt`, `Voxtral`, plus cloud engines `OpenAi`, `OpenAiCompatible`, `Groq`, `Deepgram`, `AssemblyAi`, `ElevenLabs`, `Speechmatics`, `Soniox`, `Reson8`, `Gladia`, `CloudflareAsr`, and `GoogleCloudStt`
- LLM providers — `Claude`, `OpenAi`, `OpenAiCompatible`, `OpenRouter`, `Gemini`, `GemmaLocal`, `Groq`, `Cerebras`, `Cohere`, and `Fireworks`
- Action plugins — `Linear` and `Obsidian`
- Post-processing plugins — `Script` (run a shell command against the transcription)
- Memory storage plugins — `FileMemory` (local JSON) and `OpenAiVectorMemory` (embedding-backed recall)
- Companion plugins — `Webhook` notifications

The Linux build currently deploys bundled plugins from `plugins/` into the app output, then copies them into the user plugin directory on first run if they are missing.

Plugins that own user-editable collections (Webhook, Script) expose per-plugin collection settings under `PluginData/<plugin-id>/` so their entries survive plugin reinstalls and the host can edit them through the settings UI without round-tripping a plugin process.

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

Copyright and attribution — see [NOTICE](NOTICE). TypeWhisper for Linux is © 2026 Excel on the Web and incorporates code from the upstream TypeWhisper project (© 2026 TypeWhisper).
