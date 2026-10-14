---
name: verify-typewhisper-linux
description: Verify TypeWhisper Linux changes with the repository build and tests, real app commands, local audio transcription, and repeatable latency, quality, and GPU checks.
---

# Verify TypeWhisper Linux

Use this skill after changing the Linux app, its CLI or API, plugins, model loading, acceleration, audio capture, packaging, or user settings. Start with the checks for the changed feature. Run the full CI checks when a change crosses project boundaries or when a release needs full coverage.

## Launch

The repository targets .NET 10. The requested SDK is set in `global.json`. The app needs a Linux graphical session to show its window and an available microphone for live dictation. The LibriSpeech helper also needs `ffprobe` from FFmpeg. GPU sampling needs `amdgpu_top` on AMD or `nvidia-smi` on NVIDIA.

On this host, the SDK is at `/tmp/typewhisper-dotnet/dotnet`. If `dotnet` is not already on `PATH`, run `export PATH="/tmp/typewhisper-dotnet:$PATH"` in the terminal before using the commands below.

From the repository root, restore and build the app and CLI:

```bash
dotnet restore TypeWhisper.slnx
dotnet build TypeWhisper.slnx -c Debug --no-restore -p:DeployBundledLinuxPlugins=false
dotnet build src/TypeWhisper.Cli/TypeWhisper.Cli.csproj -c Debug --no-restore
```

Check whether a TypeWhisper instance is already running:

```bash
uv run --no-project scripts/verification/doctor.py
```

If the doctor reports a healthy running instance, use it for read-only checks. Do not launch a second instance against the same control socket. If there is no running instance, start the build from the repository root:

```bash
dotnet src/TypeWhisper.Linux/bin/Debug/net10.0/typewhisper.dll --minimized
```

The command stays in the foreground. In another terminal, run the doctor again. The app is ready when `app.control_status.ok` is `true`. Stop only the process started by this command with Ctrl+C. Do not stop an instance that was already running before the check.

## Repository checks

The Linux pull request workflow in `.github/workflows/pr-ci-linux.yml` is the source of truth for the full test set. In a clean checkout, run:

```bash
dotnet restore TypeWhisper.slnx
dotnet restore src/TypeWhisper.Cli/TypeWhisper.Cli.csproj
dotnet build TypeWhisper.slnx -c Release --no-restore
dotnet build src/TypeWhisper.Cli/TypeWhisper.Cli.csproj -c Release --no-restore
dotnet test tests/TypeWhisper.Core.Tests/TypeWhisper.Core.Tests.csproj -c Release --no-build
dotnet test tests/TypeWhisper.PluginSystem.Tests/TypeWhisper.PluginSystem.Tests.csproj -c Release --no-build
dotnet test tests/TypeWhisper.Linux.Tests/TypeWhisper.Linux.Tests.csproj -c Release --no-build
dotnet test tests/TypeWhisper.Cli.Tests/TypeWhisper.Cli.Tests.csproj -c Release --no-build
dotnet test tests/TypeWhisper.Integration.Tests/TypeWhisper.Integration.Tests.csproj -c Release --no-build
```

On a developer machine with an active app install, add `-p:DeployBundledLinuxPlugins=false` to local builds. The default build target deploys bundled plugins into user data. Run only the test projects relevant to a narrow change; run every command above for cross-project changes and release checks. Use the workflow's hang timeout and result artifact options when diagnosing a slow or stuck CI run.

## Doctor

Run `uv run --no-project scripts/verification/doctor.py` before interacting with the app. It reports the source and SDK versions, app process and control status, API port and token presence, and whether the settings, model, log, and audio paths exist. It never prints API token contents or settings values.

The doctor reads `$XDG_DATA_HOME/TypeWhisper/settings.json` and `$XDG_CONFIG_HOME/typewhisper/api-discovery.json`, using the standard home-directory defaults when those environment variables are unset. It checks the API discovery port and Unix socket when the discovery file exists. A present socket that refuses a connection is stale and does not prove the API is ready.

The app can run with the API server disabled. That is a valid state for desktop checks. Enable the API server in the app's Settings page only when a CLI transcription or HTTP API check needs it. The discovery file includes a token. Do not print, copy, or save that token in evidence.

## Drive

Use the real user path for the changed feature. Do not use private setters or test-only endpoints as proof of app behavior.

- Use `typewhisper status` to inspect app state without starting or stopping a recording.
- Use `typewhisper record start` and `typewhisper record stop` only when a microphone check is needed and the user has approved a spoken test phrase. Check the returned session state and the resulting history entry.
- Use `typewhisper-cli transcribe` with a WAV path and the options `--language en --json --no-corrections` for file transcription. The app's local API server must be enabled.
- Use the Settings window to select models and acceleration backends. Check `acceleration.activeBackend` from `typewhisper-cli status --json` to confirm the active backend after model load. A selected preference alone does not prove that the backend ran.
- Use `scripts/verification/benchmark_transcription.py` for a fixed set of reference recordings. The runner calls the same CLI path a user uses.

Use the feature map in `features/README.md` to choose the drive steps and observable end state. Record the app version, commit, model, backend, runtime, device, driver, and corpus for each performance comparison.

## Evidence

Save evidence under a date-named directory such as `artifacts/verification/2026-10-13-asr/`. Keep command output, test result files, benchmark JSON, and GPU samples together. Benchmark JSON contains reference and hypothesis text. Use the public benchmark corpus and do not save credentials or private recordings there.

For each check, record the exact command or visible user action, the state before and after, the user-visible output and any files or history entries that changed, and the artifact path. Note whether an artifact contains private audio or transcript text.

For ASR comparisons, report these values separately:

- First request latency, which can include a cold model load.
- Warm complete-result median and p95.
- Real-time factor for the same audio set.
- Word error rate and character error rate against the same references.
- GPU activity and the active execution backend.

The benchmark runner measures complete CLI responses, including a new CLI process and local API request per recording. It does not measure model-only time or streaming first-token time. An offline model such as Parakeet returns text after decoding the full recording. For a streaming model, measure time from the first captured audio frame to the first visible partial text, then measure final-result time separately. Do not compare those values as if they were the same measure.

## Cleanup

Do not clear models, history, settings, or user recordings as part of verification. Keep benchmark evidence after each run. Remove only temporary files that the current run created and that contain no evidence. If this run started the foreground app, stop that app with Ctrl+C. Leave any earlier app process running.

After testing, run the doctor again and record whether the app is idle or recording. If a test started a recording and did not finish it, cancel that test session. Do not delete the resulting audio or history entry.

## Helpers

The Python helpers use Python 3 standard-library modules. The corpus helper also calls `ffprobe` to check clip length.

Inspect the current machine and app state:

```bash
uv run --no-project scripts/verification/doctor.py
```

Prepare the fixed 50-clip LibriSpeech set, then run the benchmark from its JSON Lines manifest. Each line contains `id`, `audio`, `reference`, and `duration_seconds`. The audio paths are relative to the manifest.

```bash
uv run --no-project scripts/verification/prepare_librispeech.py
mkdir -p "artifacts/verification/$(date +%F)-webgpu"
uv run --no-project scripts/verification/benchmark_transcription.py \
  --manifest benchmarks/data/librispeech-50.jsonl \
  --engine sherpa-onnx \
  --model parakeet-tdt-0.6b-v3 \
  --expect-backend webgpu \
  --language en \
  --cli src/TypeWhisper.Cli/bin/Debug/net10.0/typewhisper-cli \
  --output "artifacts/verification/$(date +%F)-webgpu/benchmark.json"
```

The corpus helper downloads the LibriSpeech test-clean and test-other archives, selects 25 speakers from each split with a fixed seed, and extracts only the selected audio. The audio and manifest stay under the ignored `benchmarks/data/` directory. The API server must be enabled and the selected model must be available. Select the backend in Settings and load the model before running the benchmark. The runner checks the active backend through the API and stops if it does not match `--expect-backend`.

For AMD GPU evidence, start this in a second terminal before the benchmark and stop it with Ctrl+C after the run:

```bash
amdgpu_top --json --select-apu --single-gpu -s 250 \
  > "artifacts/verification/$(date +%F)-webgpu/amdgpu-top.jsonl"
```

Use `vulkaninfo --summary` to capture Vulkan device and driver details. Use `nvidia-smi` for an NVIDIA device. A utilization sample proves device activity, not that every model operation ran on the GPU. Pair it with the active backend and provider or runtime logs.
