# Third-party notices

The full license texts are in `Licenses/`.

## Bundled

- GigaAM v3 feature extractor (`gigaam_v3_preprocessor.onnx`): from onnx-asr 0.12.0 by Ilya Stupakov, MIT, `onnx_asr/preprocessors/data/gigaam_v3.onnx`, SHA-256 `919c3bc8a434b6e733e5fe5dcd247974d0a7b1f2bc5b6995b6ad55f7225e014d`. https://github.com/istupakov/onnx-asr. License: `Licenses/onnx-asr-LICENSE.txt`.
- ONNX Runtime 1.24.1 (NuGet `Microsoft.ML.OnnxRuntime`): Microsoft Corporation, MIT. https://github.com/microsoft/onnxruntime.

## Downloaded on first use

The model weights are not bundled. The plugin downloads the int8 GigaAM v3 end-to-end RNN-T export (`v3_e2e_rnnt_encoder.int8.onnx`, `v3_e2e_rnnt_decoder.int8.onnx`, `v3_e2e_rnnt_joint.int8.onnx`, `v3_e2e_rnnt_vocab.txt`) from https://huggingface.co/istupakov/gigaam-v3-onnx (MIT), pinned to revision `322c3b29492673eb7d0b434bfa9dfb8653e34d02` and to the SHA-256 of every file. The export is based on GigaAM by Sber (GigaChat Team), MIT, https://github.com/salute-developers/GigaAM. License: `Licenses/GigaAM-LICENSE.txt`.

The transducer decoding follows onnx-asr's greedy RNN-T search; this is an independent C# implementation of the same steps, ported from upstream TypeWhisper (typewhisper-win #694). The model files are not modified.
