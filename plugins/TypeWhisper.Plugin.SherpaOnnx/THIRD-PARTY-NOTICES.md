# Model notices

The models are downloaded on demand and are not included in this repository. Each file is pinned to an exact size and SHA-256 in `SherpaOnnxPlugin.cs`.

- **Parakeet Ultra 0.6B** — [moondream/parakeet-ultra](https://huggingface.co/moondream/parakeet-ultra) by Moondream, a retrained [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3). Licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The int8 sherpa-onnx export is upstream TypeWhisper's (GitHub `TypeWhisper/typewhisper-win`, release `model-parakeet-ultra-int8-v1`), mirrored unchanged in this repository's release of the same name.
- **Parakeet TDT 0.6B** — [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) by NVIDIA. Licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The int8 sherpa-onnx export is [csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8).
- **Canary 180M Flash** — [nvidia/canary-180m-flash](https://huggingface.co/nvidia/canary-180m-flash) by NVIDIA. Licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The int8 sherpa-onnx export is [csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8).

The exports are int8-quantized ONNX conversions of the original NeMo checkpoints.

## Vulkan runtime and GGUF models

Neither the runtime nor the GGUF models are part of this package. When Vulkan is chosen as the acceleration, the plugin downloads them on the next Parakeet load and checks each against a pinned size and SHA-256 before it is used.

- **transcribe.cpp 0.3.1** — the transcribe.cpp authors, [MIT](https://github.com/handy-computer/transcribe.cpp). The plugin downloads the `transcribe-native-0.3.1-linux-x86_64-cpu-vulkan` release archive and keeps its `.so` libraries, `contract.json` and the license texts in its `licenses` folder.
- **ggml** (vendored in transcribe.cpp) — the ggml authors, [MIT](https://github.com/ggml-org/ggml).
- **miniz** (vendored in transcribe.cpp) — Rich Geldreich, Tenacious Software LLC, RAD Game Tools and Valve Software, [MIT](https://github.com/richgel999/miniz).
- **Parakeet TDT 0.6B GGUF** — NVIDIA's parakeet-tdt-0.6b-v3, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), converted to GGUF and quantized to Q8_0 by the transcribe.cpp project ([handy-computer/parakeet-tdt-0.6b-v3-gguf](https://huggingface.co/handy-computer/parakeet-tdt-0.6b-v3-gguf), revision `90f082450fcbacdb54e5900c44ef697c9ea59622`).
- **Parakeet Ultra 0.6B GGUF** — Moondream's parakeet-ultra, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), converted by upstream TypeWhisper from the same checkpoint as the ONNX export with transcribe.cpp 0.3.1's `convert-parakeet.py` and quantized to Q8_0 (GitHub `TypeWhisper/typewhisper-win`, release `model-parakeet-ultra-gguf-v1`).
