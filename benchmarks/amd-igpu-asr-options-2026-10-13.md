# AMD iGPU ASR options

## Recommendation

Keep CPU as the default for Sherpa Parakeet. Add WebGPU as an explicit option for people who want the GPU to do some inference work. Keep Vulkan Whisper.cpp as the speed-first GPU option when its lower accuracy is acceptable.

The tested machine is an AMD Radeon 780M integrated GPU (Hawk Point, RADV/Vulkan) on CachyOS. The audio set has 50 LibriSpeech test-clean/test-other utterances, 25 speakers per split, and 404.44 seconds of audio. The same set was used for each compared run where the model supported it.

## Results

| Engine and model | Device | First text / decode latency | p95 | RTF | WER | GPU evidence |
|---|---|---:|---:|---:|---:|---|
| Sherpa Parakeet TDT int8, app-matched Sherpa 1.12.23 + ORT 1.24.4 | CPU | 174 ms after stop | 318 ms | 0.0245 | Not valid in this timing run | CPU |
| Sherpa Parakeet TDT int8, app-matched Sherpa 1.12.23 + ORT 1.24.4 | WebGPU | 656 ms after stop | 1,357 ms | 0.0866 | Not valid in this timing run | WebGPU session created successfully; see separate GPU sampling below |
| Sherpa Parakeet TDT int8, Sherpa 1.13.4 + ORT 1.27.0 | CPU | 289 ms per utterance | 567 ms | 0.0390 | 3.99% | CPU |
| Sherpa Parakeet TDT int8, Sherpa 1.13.4 + ORT 1.27.0 | WebGPU | 921 ms per utterance | 1,733 ms | 0.1220 | 4.08% | Mean 26.9% system GPU busy; peak 58% |
| Whisper.cpp base, Vulkan | Vulkan GPU | 190 ms warm completion interval; 431 ms for first result including model load | 350 ms | 0.0258 | 7.07% | Mean 61.1% system GPU busy; peak 70% |
| Whisper.cpp base, CPU | CPU | 432 ms warm completion interval; 581 ms for first result including model load | 578 ms | 0.0546 | 6.97% | Mean 1.6% system GPU busy |
| ONNX-ASR Parakeet TDT int8 | CPU | 236 ms | 378 ms | 0.0316 | 3.80% | CPU |
| ONNX-ASR Parakeet TDT int8 | WebGPU | 628 ms | 1,175 ms | 0.0903 | 3.89% | ORT profiles assigned encoder and decoder/joiner nodes to WebGPU |
| Sherpa streaming Zipformer EN int8 | CPU | First text: 1.21 s p50, 1.51 s p95 from speech start | Final text was available immediately after stop | 0.0415 compute | 9.33% | CPU; streaming comparison only |

The Sherpa 1.12.23 app-matched timings used the exact native runtime we will package. Transcript capture in that particular timing script read stdout, while this Sherpa CLI emitted transcripts on stderr. Its latency measurements are usable, but its reported 100% WER/CER is a capture error and must be ignored. The 1.13.4 rows use a corrected capture over the same 50 utterances. Accuracy values from different engine/model families are not a direct model-quality ranking.

For current Parakeet TDT, transcription is offline. It emits no partial text. Therefore, “first text after stop” is the full decode latency shown above. Time from speech start also includes the recording duration. The streaming Zipformer row is a different model, with lower accuracy; it demonstrates the first-partial measurement but is not interchangeable with Parakeet.

## What the measurements say

- Sherpa WebGPU moved work onto the 780M and kept WER close to Sherpa CPU in the corrected 1.13.4 run. It was about 3.2 times slower at median latency and about 3.1 times slower by aggregate RTF. Use it to free CPU resources, not to reduce wait time.
- Whisper.cpp Vulkan was the fastest tested GPU route. Its model was less accurate than Parakeet, so it is a speed/quality choice rather than a drop-in backend comparison.
- ONNX-ASR WebGPU also ran slower than CPU on this iGPU. Profiling confirmed that it did execute graph nodes on WebGPU, so the slowdown is not evidence of CPU-only fallback.
- A Parakeet FP16-encoder/int8-decoder CPU run reached 3.26% WER and 0.010 CER. It was slower than int8 CPU at median latency and used no GPU. This remains the best measured quality point in this small sweep.

## Backend options for this machine

1. **WebGPU through Vulkan:** selected for Sherpa Parakeet as an opt-in backend. ONNX Runtime's native WebGPU EP uses Dawn and Vulkan on Linux. It needs a supported Vulkan device, not ROCm. ONNX Runtime assigns supported graph nodes to the EP and leaves unsupported nodes on CPU, so this is GPU offload, not full-GPU execution.
2. **Vulkan in Whisper.cpp:** available now and the best measured GPU speed path. It changes the ASR engine/model, so compare it as an option rather than the same backend on the same graph.
3. **ROCm/MIGraphX:** technically viable in current AMD software for supported Radeon 780M environments, but this CachyOS box has no ROCm/HIP or MIGraphX runtime installed. AMD's current ROCm matrix lists the 780M for supported Ubuntu and Windows configurations; CachyOS is not listed. ONNX Runtime now marks its ROCm EP as deprecated and lists MIGraphX separately. We did not measure MIGraphX here. Do not select it until a compatible stack is installed and its Parakeet graph coverage, quality, memory use, and latency are measured locally.
4. **CPU ONNX Runtime:** remains the fastest Sherpa path on this box. Keep it as the default, especially for users who prefer the lowest latency.

## Implementation details

The bundled Sherpa runtime stays at the app's Sherpa 1.12.23 API version. The build script adds the WebGPU provider registration bridge and builds against ONNX Runtime 1.24.4 plus the matching WebGPU plugin package. The UI exposes WebGPU only for supported Sherpa transducer models when the packaged provider library is present. CPU remains the fallback if provider initialization fails.

Sources:

- [ONNX Runtime native WebGPU provider](https://onnxruntime.ai/docs/execution-providers/WebGPU-ExecutionProvider.html)
- [ONNX Runtime plugin provider registration](https://onnxruntime.ai/docs/execution-providers/plugin-ep-libraries/usage.html)
- [ONNX Runtime execution provider support](https://onnxruntime.ai/docs/execution-providers/)
- [AMD ROCm compatibility matrix](https://rocm.docs.amd.com/en/docs-7.14.1/compatibility/compatibility-matrix.html)
- [Sherpa-ONNX WebGPU support issue](https://github.com/k2-fsa/sherpa-onnx/issues/3665)
