# ASR quality and performance

## Sub-features

- A fixed corpus measures first request time and warm complete-result latency.
- The same references measure word error rate and character error rate.
- GPU activity and the active execution backend provide evidence of GPU use.
- Streaming first-text time stays separate from offline final-result time.

## How to get to it (user POV)

Use the local benchmark when changing a speech model, quantization, runtime, CPU or GPU backend, audio path, or streaming behavior. Select each backend in the app and run the same audio manifest with the same model settings.

## Driving it with the CLI benchmark helper

Prepare a JSON Lines manifest with one object per recording. Each object must have `id`, `audio`, `reference`, and `duration_seconds`. Use the same recordings and references for every run. Keep audio paths relative to the manifest when possible.

Run `scripts/verification/benchmark_transcription.py` once for each backend. Set `--expect-backend` to the backend reported by the app. Save output JSON under `artifacts/verification/` and sample GPU activity in a separate terminal with `amdgpu_top --json --select-apu --single-gpu -s 250`.

Compare first request latency, warm median, p95, aggregate real-time factor, WER, and CER. Preserve all hypotheses in the output JSON so a quality change can be reviewed. Record hardware, driver, app commit, model, quantization, runtime versions, thread settings, and whether the run was cold or warm.

For live streaming tests, play a fixed recording into a virtual microphone or use an approved spoken phrase. Capture the time of the first audio frame, the first visible partial text, and the final result. Keep this separate from file-transcription timing. Sherpa Parakeet TDT is offline and emits no partial text, so its first text arrives with its completed decode.

## Gotchas

- Compare WER or CER across CPU and GPU for the same model and decoding settings. Different model families do not share one quality ranking.
- Check transcript capture before accepting scores. A process can write recognized text to stderr instead of stdout.
- Include model load time in a separate cold measurement. Do not mix it with warm per-clip latency.
- Use the same audio duration sum when computing real-time factor.
- GPU counters can include desktop rendering and other processes. Sample idle use and inference use, then report both.
- Use `just prepare-corpus` for the fixed LibriSpeech manifest under `benchmarks/data/`. Earlier one-off clips in `/tmp/typewhisper-benchmark` disappear on reboot.
- For dictation timing, read the `performance` block of `/v1/dictation/transcription` from `just live-smoke`. It separates transcription, each processing step, queue wait, and delivery. It is measured inside the app and does not include the target app drawing the text.
