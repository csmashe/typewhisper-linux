# GigaAM

Local Russian speech recognition with GigaAM v3 by Sber, ported from upstream TypeWhisper (typewhisper-win #694) with the same plugin id (`com.typewhisper.gigaam`), provider id (`gigaam`) and model id (`gigaam-v3-e2e-rnnt`).

## Model

- The end-to-end RNN-T variant, which outputs punctuation and capitalization. Russian only: `SupportedLanguages` is `["ru"]`, so the language picker offers only automatic detection and Russian, and audio in other languages is transcribed as Russian.
- The int8 export from [`istupakov/gigaam-v3-onnx`](https://huggingface.co/istupakov/gigaam-v3-onnx) (about 216 MB), downloaded on first use from a pinned revision into `PluginAssetDirectory/Models/gigaam-v3-e2e-rnnt`. Each file is downloaded to a `.partial` file (resumed after an interruption) and only gets its final name once its SHA-256 matches.
- The 44 KB feature extractor (`gigaam_v3_preprocessor.onnx`, from onnx-asr 0.12.0) ships with the plugin.

## Runtime

ONNX Runtime 1.24.1 on the CPU, the same package the Parakeet CTC and Supertonic plugins use. The shared `OnnxRuntimeNativeLibrary` resolver binds the plugin's own `libonnxruntime.so`, never sherpa-onnx's copy. The preprocessor turns 16 kHz audio into 64 log-mel features, the encoder runs once per chunk and a greedy transducer loop drives the decoder and joint networks (at most three tokens per frame, as in onnx-asr). The host runs the plugin in its transcription worker process, so a native crash cannot end the app.

Recordings longer than 20 seconds are cut at the quietest 200 ms near an even split. If a cut falls mid-sentence, the next chunk starts lowercase instead of with GigaAM's sentence capital. Tokens are joined like SentencePiece; unlike onnx-asr's detokenizer, the space before dashes and opening quotes is kept (`Европа — …`).

## Live test

`GigaAmLiveModelTests` runs the real model when both variables are set and skips otherwise:

```bash
export GIGAAM_MODEL_DIR=/path/to/gigaam-v3-onnx   # the four downloaded files
export GIGAAM_AUDIO=/path/to/russian-16k.wav      # 16 kHz mono 16-bit
dotnet test tests/TypeWhisper.PluginSystem.Tests --filter GigaAmLiveModelTests
```
