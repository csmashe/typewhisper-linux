---
name: verify-typewhisper-linux
description: Verify TypeWhisper Linux changes with the repository build and tests, an isolated real app instance, local audio through a virtual microphone, rendered UI checks, quality evals, and repeatable latency and GPU measurements.
---

# Verify TypeWhisper Linux

Use this skill after changing the Linux app, its CLI or API, plugins, model loading, acceleration, audio capture, cleanup, text delivery, the UI, packaging, or settings. Start with the checks for the changed feature in `features/README.md`. Run the full CI set when a change crosses project boundaries.

Five layers answer different questions. None can stand in for another.

| Layer | Question | Entry point |
|---|---|---|
| Tests | Is the code correct? | `just ci`, or one `just test-*` recipe |
| Live verification | Does the user path work in the real app? | `just live-smoke` and the feature guides |
| UI | Does every screen render readable, named controls? Can a person act on it? | `just ui-check`, `just ui-live` |
| Quality evals | Is the output text good? | `just eval-capture`, then the `just eval-*` review recipes |
| Performance | How fast is it, and where does the time go? | `just benchmark`, the `performance` field of a dictation result |

## Launch

The repository targets .NET 10 (`global.json`). The `justfile` finds the SDK on `PATH` or at `/tmp/typewhisper-dotnet/dotnet`; set `DOTNET` to choose another. Built CLI executables can also need `DOTNET_ROOT=/tmp/typewhisper-dotnet`.

```bash
just restore          # fresh checkout only
just build-release    # app, CLI, plugins, and all test projects
```

Use an isolated instance for anything that changes app state. `scripts/verification/isolated_app.py` starts the built app with its own `HOME`, XDG data, config, state, and cache folders under the evidence directory, a short private runtime folder in `/tmp`, its own API port and token, and optionally a PulseAudio null sink as its microphone. It never reads or writes the user's settings, history, or recordings. When asked, it reflink-copies a plugin's model folder from the user's `PluginData`; that source is only read.

```bash
just live-smoke                      # dictation with a fake cleanup LLM, clipboard read-back
just live-smoke artifacts/verification/$(date +%F)-no-llm "--llm none"   # arguments are positional
uv run --no-project scripts/verification/isolated_app.py --workdir artifacts/verification/$(date +%F)-manual \
  --configuration Release --plugin-data com.typewhisper.sherpa-onnx --virtual-mic
```

The last command prints the port and keeps the instance running until Ctrl+C. The instance's published token is in `<workdir>/instance/config/typewhisper/api-discovery.json`.

The user's own instance usually runs the Debug build. Do not run `just build` while it runs; that overwrites assemblies under a live process. Build Release for isolated runs. Restart the user's instance with `just restart` only when the user asks for it.

## Doctor

Run `just doctor` before driving anything. It reports the source commit and SDK, the app process and control status, the API port, discovery file, and socket state, token presence (never its value), and data folders, including model files per plugin under `PluginData`. Exit code 2 means no healthy running instance.

Inside a sandbox, uv may fail to write its cache; set `UV_CACHE_DIR=/tmp/uv-cache`. A sandbox that hides host processes or `/tmp` makes the doctor report no app and no SDK; rerun it on the host before concluding anything.

The user's app can run with its API disabled. That is a valid state. Do not enable it to verify a change; use an isolated instance instead.

## Drive

Use the real user path. Internal setters and test-only endpoints are not proof of user-facing behavior.

- Control socket: `typewhisper status` reads state without recording. `typewhisper record start|stop|toggle|cancel` drives dictation.
- HTTP API on an isolated instance: `/v1/dictation/start`, `/v1/dictation/stop`, and `/v1/dictation/transcription?sessionId=` for dictation; `/v1/transcribe/local-file` for files; `/v1/models/load` to load a model; `/v1/text/process` to run raw text through the final dictation pipeline without inserting it.
- Microphone: play public audio into the isolated instance's null sink with `VirtualMic.play`. `IsolatedApp.capture_sources()` must list only the fixture monitor. Record a physical microphone only with an approved test phrase.
- Delivery: the session result's `state: ready` and a successful insertion return do not prove delivery. Read the text back from the destination; `live_smoke.py` reads the clipboard and compares it with the session text.
- Settings: the Settings window, or settings written into an isolated instance before launch. Confirm `acceleration.active_backend` from `/v1/status` after model load; a selected preference does not prove the backend ran.

## Evidence

Save evidence under `artifacts/verification/<date>-<topic>/` and quality runs under `artifacts/evals/<date>-<topic>/`. Every helper writes a `run.json` with the commit, branch, dirty files, app version, host, and run settings. Compare two runs only when their manifests match except for the variable under test.

For each check, record the command or user action, the state before and after, the visible output and changed files or history entries, and the artifact path. Note when an artifact holds transcript text. An isolated instance folder holds a throwaway API token for a stopped instance; do not copy real credentials into one.

Report transcription, cleanup, and delivery times separately. For ASR comparisons report first-request latency, warm median and p95, real-time factor, WER, CER, and the active backend. The benchmark measures complete CLI responses, not model-only or first-partial time.

For cleanup, dictionary, profile, or streaming-quality changes, also use `evals/dictation/README.md`. Run `just eval-tests`, capture actual raw and final text with the same cases, and keep manual pass/fail critiques. Use the audio scenarios for segment boundaries and later corrections. Separate upstream recognition errors from cleanup errors. An LLM judge is optional and advisory until checked against human labels. Do not send transcripts to a judge during ordinary build or verification commands. Record model, prompt, dictionary, profile, and backend versions with each quality run.

## Cleanup

Isolated runs clean up after themselves: the helpers stop only the process they started (by process group), unload only the null sink they loaded, remove only their `/tmp/tw-verify-*` runtime folder, and restore the clipboard they saved. Check afterwards:

```bash
pactl list short modules | grep -c null-sink     # 0 unless something else loaded one
ls -d /tmp/tw-verify-* 2>/dev/null               # nothing
```

Do not clear models, history, settings, or recordings. Keep evidence folders. Leave the user's instance running. If a run is killed before its cleanup, stop only the leftover isolated process (its environment has `XDG_DATA_HOME` under the evidence folder) and unload only the `tw_verify_fixture` sink. A `wl-copy` process serving the restored clipboard is expected; do not kill it.

## Helpers

All Python helpers use the standard library and run with `uv run --no-project`. Their tests run with `just eval-tests`.

| Helper | Purpose |
|---|---|
| `scripts/verification/doctor.py` | Read-only health report (`just doctor`) |
| `scripts/verification/isolated_app.py` | Isolated instance, virtual mic, fake OpenAI-compatible LLM, run manifest |
| `scripts/verification/live_smoke.py` | End-to-end dictation with clipboard read-back (`just live-smoke`) |
| `scripts/verification/capture_text_evals.py` | Eval capture through `/v1/text/process` (`just eval-capture`) |
| `scripts/verification/benchmark_transcription.py`, `compare_benchmarks.py` | File ASR benchmark and before/after comparison (`just benchmark`) |
| `scripts/verification/prepare_librispeech.py` | Fixed public corpus (`just prepare-corpus`) |
| `tests/TypeWhisper.UiTests` | Rendered Settings checks and PNGs (`just ui-check`) |
| `scripts/verification/hidden_desktop.py` | Hidden headless desktop, AT-SPI actions, RemoteDesktop input, screenshots (`just ui-live`, `just ui-shortcuts`, `just ui-streaming`) |

The fake LLM serves `GET /v1/models` and `POST /v1/chat/completions` only. Any other path returns 404 and is recorded, so a malformed base URL fails instead of passing. At Medium or High cleanup, a run with no cleanup LLM call means cleanup silently fell back to Light; the capture helper marks those rows as errors.

## Not yet mapped

These areas have user-facing behavior but no live recipe proven yet. Add a feature guide only after driving one live.

- Streaming and audio quality, deferred by the user to a later pass. `just ui-streaming` watches the overlay's live-preview text over AT-SPI while a public clip plays, but it has not been run. Segment-boundary quality needs the eval audio scenarios recorded.
- Plugin catalog install and update from the network, and provider setup by clicking through the Plugins section. Provider activation and routing are covered in `features/plugins-and-providers.md`.
- Container install, upgrade, and removal of packages on this host. It needs `rpm-tools` installed; CI runs it. See `features/packaging-and-install.md`.
