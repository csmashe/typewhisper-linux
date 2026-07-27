# TypeWhisper for Linux

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Linux](https://img.shields.io/badge/Linux-Desktop-FCC624.svg)](https://kernel.org)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Speech-to-text and AI text processing for the Linux desktop. This repository is a Linux desktop port forked from the TypeWhisper project, which provides macOS and Windows versions. I ported it so I could use TypeWhisper on Linux, and I am making this branch available for other Linux users who want the same.

If the TypeWhisper project releases an official Linux version, or if this port is merged into the main TypeWhisper branch, I plan to use the upstream Linux version instead. Until then, this branch exists as a practical Linux port adapted around Avalonia, Linux desktop services, and Linux-friendly install and startup behavior.

Press a key, talk, and have clean, punctuated text land in whatever app you're in — tuned to feel as close to [Wispr Flow](https://wisprflow.ai/) as possible on Linux. TypeWhisper lets you dictate into other applications, transcribe audio files, record longer WAV sessions, apply dictionary, snippet, and spoken-number post-processing, and run prompt-based AI text actions through plugins.

## Documentation

**The full, step-by-step documentation lives in the [Wiki](https://github.com/csmashe/typewhisper-linux/wiki).** This README is a short overview — the wiki has the how-to detail, per-distro setup, and troubleshooting.

- **Getting started** — [Installation](https://github.com/csmashe/typewhisper-linux/wiki/Installation) · [Quick Start](https://github.com/csmashe/typewhisper-linux/wiki/Quick-Start) · [Setup Wizard](https://github.com/csmashe/typewhisper-linux/wiki/Setup-Wizard) · [Requirements](https://github.com/csmashe/typewhisper-linux/wiki/Requirements)
- **Using it** — [Dictation](https://github.com/csmashe/typewhisper-linux/wiki/Dictation) · [Global Hotkeys](https://github.com/csmashe/typewhisper-linux/wiki/Global-Hotkeys) · [Text Insertion](https://github.com/csmashe/typewhisper-linux/wiki/Text-Insertion) · [Profiles](https://github.com/csmashe/typewhisper-linux/wiki/Profiles) · [Text Cleanup](https://github.com/csmashe/typewhisper-linux/wiki/Text-Cleanup)
- **Platform** — [Wayland Notes](https://github.com/csmashe/typewhisper-linux/wiki/Wayland-Notes) · [GPU & CUDA](https://github.com/csmashe/typewhisper-linux/wiki/GPU-and-CUDA) · [Desktop Integration](https://github.com/csmashe/typewhisper-linux/wiki/Desktop-Integration) · [Tested Configurations](https://github.com/csmashe/typewhisper-linux/wiki/Tested-Configurations) · [Troubleshooting](https://github.com/csmashe/typewhisper-linux/wiki/Troubleshooting)
- **Automation** — [HTTP API](https://github.com/csmashe/typewhisper-linux/wiki/HTTP-API) · [CLI](https://github.com/csmashe/typewhisper-linux/wiki/CLI)
- **Plugins** — [Plugins](https://github.com/csmashe/typewhisper-linux/wiki/Plugins) · [Transcription Engines](https://github.com/csmashe/typewhisper-linux/wiki/Transcription-Engines) · [LLM Providers](https://github.com/csmashe/typewhisper-linux/wiki/LLM-Providers) · [Plugin SDK](https://github.com/csmashe/typewhisper-linux/wiki/Plugin-SDK)
- **Project** — [Data & File Paths](https://github.com/csmashe/typewhisper-linux/wiki/Data-and-File-Paths) · [Roadmap](https://github.com/csmashe/typewhisper-linux/wiki/Roadmap) · [Contributing](https://github.com/csmashe/typewhisper-linux/wiki/Contributing)

## What it does

- **Global dictation** with Toggle, Push-to-talk, and Hybrid activation modes, a configurable recording overlay, recent-transcriptions and transform-selection hotkeys, and cancel-in-flight. See [Dictation](https://github.com/csmashe/typewhisper-linux/wiki/Dictation) and [Global Hotkeys](https://github.com/csmashe/typewhisper-linux/wiki/Global-Hotkeys).
- **Plugin-backed transcription** — local engines (whisper.cpp, sherpa-onnx, …) and cloud engines, with optional real-time websocket streaming and token-by-token LLM response streaming. See [Transcription Engines](https://github.com/csmashe/typewhisper-linux/wiki/Transcription-Engines).
- **Local GPU acceleration** — optional NVIDIA CUDA for the bundled whisper.cpp and sherpa-onnx engines, with the runtime downloaded on demand rather than bundled — resumable downloads, and an in-app reset to recover from a bad cache. See [GPU & CUDA](https://github.com/csmashe/typewhisper-linux/wiki/GPU-and-CUDA).
- **Smart per-app text insertion** — `Auto` / `Clipboard Paste` / `Direct Typing` / `Copy Only` keyed by process name, with session-aware Wayland backends (`wtype` / `ydotool` / `xdotool`). See [Text Insertion](https://github.com/csmashe/typewhisper-linux/wiki/Text-Insertion).
- **AI text cleanup & formatting** in the Wispr-Flow style, driven by your own LLM, with profile style presets and developer-safe formatting. See [Text Cleanup](https://github.com/csmashe/typewhisper-linux/wiki/Text-Cleanup).
- **Voice-native editing** — start a dictation with a keyphrase ("TypeWhisper") and the rest becomes a command that edits your highlighted text or writes new text at the cursor, instead of being typed verbatim. Ships disabled; enable it in [Prompts](https://github.com/csmashe/typewhisper-linux/wiki/Prompts).
- **File transcription and a recorder** — batch queues, watch folders, subtitle (SRT/VTT) export, and longer WAV captures. See [File Transcription](https://github.com/csmashe/typewhisper-linux/wiki/File-Transcription) and [Recorder](https://github.com/csmashe/typewhisper-linux/wiki/Recorder).
- **Personalization** — searchable history, a dictionary with term packs, snippets, and app/URL-matched profiles. History also has an opt-in **Inspect** panel that shows exactly what was sent to the LLM for each entry — the raw→final diff, the exact prompt, injected memory context, and the reply, with local-vs-cloud labelling. See [Profiles](https://github.com/csmashe/typewhisper-linux/wiki/Profiles) and [History](https://github.com/csmashe/typewhisper-linux/wiki/History).
- **Learns from your corrections** in the Wispr-Flow style — when you type over a dictated word in the target app to fix it, TypeWhisper silently learns the correction (via AT-SPI) and auto-applies it to future dictations. Off by default (it reads the focused field); enable it under Dictation settings, and review or remove learned entries in the dictionary.
- **A localized interface** — English, German, Spanish, or Russian, switched live (or Auto, to follow your system locale). See [General Settings](https://github.com/csmashe/typewhisper-linux/wiki/General-Settings).
- **Automation** — a local [HTTP API](https://github.com/csmashe/typewhisper-linux/wiki/HTTP-API) and an installable `typewhisper` [CLI](https://github.com/csmashe/typewhisper-linux/wiki/CLI).
- **Desktop integration** — tray icon, XDG autostart, single-instance handoff, and a user-level installer. See [Desktop Integration](https://github.com/csmashe/typewhisper-linux/wiki/Desktop-Integration).

Everything here is Linux-specific work adapted from the upstream macOS/Windows project: Wayland/X11 global hotkeys, compositor-native window and URL detection, session audio handling, and Linux packaging. The deep how-and-why for each lives in the wiki — start with [Wayland Notes](https://github.com/csmashe/typewhisper-linux/wiki/Wayland-Notes) if you're on Wayland.

## My Setup

I run this branch as my daily driver and tune it to feel as close to [Wispr Flow](https://wisprflow.ai/) as I can get on Linux: press a key, talk, and have clean, punctuated text land in whatever app I'm in.

The stack I actually use day to day:

- **Transcription** — the bundled whisper.cpp engine on the GPU (CUDA 12), running the full `large-v3` model. Since I'm running on a GPU there's headroom for it, so I moved up from `large-v3-turbo` to the full model. It's fully local, runs fine on my GTX 1070, and accurate enough that I rarely re-record.
- **Cleanup** — an OpenAI-compatible LLM server (Ollama) running on a separate machine on my LAN — an RTX 3090 box. I now run cleanup through a custom dictation-cleanup model we're still putting together; it isn't released yet (more on that below). If you want the same setup today, I recommend `mistral-small:24b` — it's what I ran before and still works well. My **Auto Clean Up Text** prompt drives the model, and it's written to clean dictation the way Wispr Flow does: strip filler words ("um", "uh", "like"), fix capitalization and punctuation, apply spoken self-corrections in place, and format lists when I clearly ask for one — without ever adding, answering, or dropping anything I actually said. The **Auto Format** profile binds that prompt to a single hotkey (`Ctrl+Alt+E`), so dictation goes straight through cleanup before it's inserted. Both ship seeded but disabled on a fresh install, so you can turn them on and point the cleanup at your own LLM.
- **Insertion** — auto-paste is on, and on GNOME Wayland the text is delivered through `ydotool`.
- **Hotkeys** — I run in **Hybrid** activation mode: a quick tap toggles recording, and holding acts as push-to-talk.

I keep the rest deliberately minimal for latency and predictability — audio ducking, media pause, sound feedback, live/streaming transcription, and silence auto-stop are all off. The cleanup LLM is the only network hop, and it lives on a separate box on my own LAN, so nothing leaves the machines I control.

**Where this is going:** the cleanup model I run now is an early, not-yet-released build of a model we're putting together purpose-built for dictation cleanup, so the behavior lives in the weights instead of being carried by a long system prompt (see [`docs/prompts/`](docs/prompts/) for the prompt-driven approach `mistral-small:24b` still uses). The goal is cleanup that's faster, more consistent, and far less sensitive to prompt wording than leaning on a general model. Until it ships, `mistral-small:24b` is the one to use — and if you find a model that works better, let me know and I will update the documentation.

## Install

Tagged releases on [GitHub Releases](https://github.com/csmashe/typewhisper-linux/releases) ship four `linux-x64` formats — **AppImage**, Debian/Ubuntu **`.deb`**, Fedora/RHEL **`.rpm`**, and a no-root **tarball** — each bundling the self-contained .NET runtime and the Linux plugins. See **[Installation](https://github.com/csmashe/typewhisper-linux/wiki/Installation)** for which format to pick and the per-format commands, and **[Requirements](https://github.com/csmashe/typewhisper-linux/wiki/Requirements)** for the optional desktop helpers (`pactl`, `playerctl`, `wtype` / `ydotool` / `xdotool`, `pw-play` / `paplay` / `aplay`, …).

Whichever format you install, the first-run [Setup Wizard](https://github.com/csmashe/typewhisper-linux/wiki/Setup-Wizard) checks what's needed and gets you set up with everything required — the typing/paste backend, the global-dictation hotkey, active-window detection, and more — so you don't have to wire it up by hand.

### Build from source

Requires the **.NET 10 SDK**.

```bash
git clone https://github.com/csmashe/typewhisper-linux.git
cd typewhisper-linux
dotnet build
dotnet run --project src/TypeWhisper.Linux
```

To install a clickable launcher and icon for the current user (publishes self-contained, bundles the Linux plugins, and registers a `.desktop` entry):

```bash
./scripts/install-linux-app.sh      # ./scripts/uninstall-linux-app.sh to remove (keeps your data; add --purge to delete it too)
```

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
├── docs/                        # Release notes and prompts
└── tests/                       # Automated tests
```

## Contributing & Support

This branch is tested on Pop!_OS (GNOME/X11), Linux Mint (Cinnamon/X11), Fedora (GNOME and KDE Wayland), and Arch (Hyprland/Wayland) — see [Tested Configurations](https://github.com/csmashe/typewhisper-linux/wiki/Tested-Configurations) for the per-setup detail. Other Wayland compositors should work via their compositor-native providers but are untested.

If you hit a setup-specific issue, please open an issue or pull request with your distribution, desktop environment, display server, reproduction steps, and any relevant logs (the About page's Error Log captures diagnostics). See [Troubleshooting](https://github.com/csmashe/typewhisper-linux/wiki/Troubleshooting) and [Contributing](https://github.com/csmashe/typewhisper-linux/wiki/Contributing).

## License

GPLv3 — see [LICENSE](LICENSE) for details. Trademark policy — see [TRADEMARK.md](TRADEMARK.md).

Copyright and attribution — see [NOTICE](NOTICE). TypeWhisper for Linux is © 2026 Excel on the Web and incorporates code from the upstream TypeWhisper project (© 2026 TypeWhisper).
