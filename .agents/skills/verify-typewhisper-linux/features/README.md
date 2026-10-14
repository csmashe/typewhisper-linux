# TypeWhisper Linux feature map

Choose a feature file after reading the changed files. Each guide gives a real user path and the result to check.

| Feature | Use when changing |
|---|---|
| [App startup and dictation control](app-startup-and-dictation.md) | Startup, tray process, control socket, microphone recording, or hotkey control |
| [Transcription, CLI, and history](transcription-cli-and-history.md) | File transcription, HTTP API, result formatting, history, or model output |
| [Models and acceleration](models-and-acceleration.md) | Model selection, backend choice, runtime loading, GPU use, or acceleration status |
| [ASR quality and performance](asr-quality-and-performance.md) | Model, runtime, quantization, streaming, latency, quality, or device comparisons |

For code-only changes, run the smallest relevant test project and build the affected project. For user-facing changes, also follow the relevant feature guide against a running app. For changes that cross app, plugin, CLI, API, and packaging boundaries, run the full CI sequence in `SKILL.md` and the feature-specific checks.
