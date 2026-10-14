# TypeWhisper Linux feature map

Choose a feature file after reading the changed files. Each guide gives a real user path and the result to check.

| Feature | Use when changing | Live recipe |
|---|---|---|
| [App startup, dictation control, and shortcuts](app-startup-and-dictation.md) | Startup, tray, control socket, microphone recording, hotkeys, shortcut recording | `just doctor`, `just live-smoke`, `just ui-shortcuts` |
| [Cleanup and text delivery](cleanup-and-delivery.md) | Rule cleanup, LLM cleanup, live cleanup, snippets, dictionary, text insertion, `/v1/text/process` | `just live-smoke`, `just eval-capture` |
| [Transcription, CLI, and history](transcription-cli-and-history.md) | File transcription, HTTP API, result formatting, history, model output | isolated instance plus `/v1/transcribe/local-file` |
| [Models and acceleration](models-and-acceleration.md) | Model selection, backend choice, runtime loading, GPU use, acceleration status | Settings plus `/v1/status` |
| [ASR quality and performance](asr-quality-and-performance.md) | Model, runtime, quantization, streaming, latency, quality, device comparisons | `just benchmark` |
| [Plugins and providers](plugins-and-providers.md) | Plugin loading, enabled state, engine or LLM provider registration, plugin settings | `just eval-capture`, `just live-smoke` |
| [UI rendering and UX flows](ui-rendering.md) | Any `.axaml` view, localization strings, layout, accessibility | `just ui-check`, `just ui-live` |
| [Packaging and install](packaging-and-install.md) | Package scripts, publish settings, bundled plugins, install and uninstall scripts | `scripts/build-linux-packages.sh`, `scripts/smoke-test-linux-packages.sh` |
| [Dictation quality evals](../../../../evals/dictation/README.md) | Cleanup meaning, fillers, repairs, dictionary terms, profiles, manual or LLM judging | `just eval-capture`, `just eval-review` |

For code-only changes, run the smallest relevant test project and build the affected project. For user-facing changes, also follow the relevant feature guide against an isolated instance. For changes that cross app, plugin, CLI, API, and packaging boundaries, run `just ci` and the feature-specific checks. `SKILL.md` lists the areas that have no proven live recipe yet.
