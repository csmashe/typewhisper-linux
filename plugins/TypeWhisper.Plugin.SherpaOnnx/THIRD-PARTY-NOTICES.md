# Model notices

The models are downloaded on demand and are not included in this repository. Each file is pinned to an exact size and SHA-256 in `SherpaOnnxPlugin.cs`.

- **Parakeet Ultra 0.6B** — [moondream/parakeet-ultra](https://huggingface.co/moondream/parakeet-ultra) by Moondream, a retrained [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3). Licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The int8 sherpa-onnx export is upstream TypeWhisper's (GitHub `TypeWhisper/typewhisper-win`, release `model-parakeet-ultra-int8-v1`), mirrored unchanged in this repository's release of the same name.
- **Parakeet TDT 0.6B** — [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) by NVIDIA. Licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The int8 sherpa-onnx export is [csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8).
- **Canary 180M Flash** — [nvidia/canary-180m-flash](https://huggingface.co/nvidia/canary-180m-flash) by NVIDIA. Licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The int8 sherpa-onnx export is [csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-canary-180m-flash-en-es-de-fr-int8).

The exports are int8-quantized ONNX conversions of the original NeMo checkpoints.
