# Models and acceleration

## Sub-features

- The Dictation settings page lists models and supported acceleration choices.
- Unsupported choices are unavailable or return to CPU with a visible reason.
- The selected model loads with the requested execution backend.
- The status view reports the active backend and any restart requirement.

## How to get to it (user POV)

Open Settings, then Dictation. Choose a local model and an acceleration option. WebGPU is an opt-in option for supported Sherpa models on Linux x64 when the provider library is packaged. CPU remains available.

## Driving it with the Settings page and local API

Select the model and backend in Settings, then load the model and run one known audio clip. Confirm the acceleration status in the UI. If the API server is enabled, run `typewhisper-cli status --json` and check `acceleration.preference`, `acceleration.activeBackend`, and `acceleration.requiresRestart`.

For WebGPU, confirm the active backend is `webgpu`; do not infer GPU use from the dropdown alone. Capture provider or runtime logs and a GPU activity sample during inference. Check that the output text remains valid and that a CPU run on the same model and clip produces the expected comparison.

## Gotchas

- Execution providers can assign only supported operations to the GPU. Other operations can run on the CPU.
- Provider initialization can fail and fall back to CPU. Record the active backend after inference.
- Some native runtimes are pinned for the process lifetime. Restart TypeWhisper when the UI reports that a restart is required.
- A high GPU utilization value does not prove that the ASR model's main operations ran on the GPU.
- The Radeon 780M benchmark in the repository report used offline Parakeet. WebGPU was slower than CPU for that test, so describe it as a CPU-resource option rather than a latency improvement.
