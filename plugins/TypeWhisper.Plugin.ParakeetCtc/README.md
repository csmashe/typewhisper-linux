# Parakeet CTC vocabulary add-on

A regular bundled TypeWhisper Linux plugin, discovered by `PluginLoader` from its own top-level plugin directory. Activation prepares the required acoustic model and tokenizer automatically. The host's `VocabularyRescoringService` invokes it after local Parakeet transcriptions when the Dictionary's **Enable acoustic dictionary boosting** setting is on. It uses enabled vocabulary terms and the main recognizer's token timings.

## Automatic model setup

Download `sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2` from the official `k2-fsa/sherpa-onnx` GitHub release `asr-models`. Verify the archive SHA-256:

`17f945007b52ccd8b7200ffc7c5652e9e8e961dfdf479cefcabd06cf5703630b`

The installer fetches the archive from `https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2` and the tokenizer from `https://huggingface.co/FluidInference/parakeet-ctc-110m-coreml/resolve/main/tokenizer.json`. The tokenizer SHA-256 is `9f7c517c0bf644b1b690ab037bab4d4c53aecd38e047e7154d011013ab9160db`. Both hashes must match before publication. Every tokenizer vocabulary ID is validated against `tokens.txt`.

Assets are staged beside `PluginAssetDirectory/model`. The archive has a 512 MiB download cap; the tokenizer has a 16 MiB cap. These are safety limits, not estimated download sizes. Only the two expected archive files are extracted, with bounded lengths and rejected traversal/link entries. Cancellation and a ten-minute setup limit remove staging files. A receipt records the verified source hashes and installed file hashes; subsequent activation checks these hashes and skips networking when the assets remain valid. Downloaded assets remain on disk after deactivation and package uninstall. Existing unmanaged files are retained in a sibling backup when verified assets are published.

An explicit `ModelDirectory` remains a read-only asset override: supply `model.int8.onnx`, `tokens.txt` and `tokenizer.json` yourself. Automatic setup never rewrites that directory. Missing or mismatched files prevent activation. Assets live under `PluginAssetDirectory/model`; the host may redirect the plugin asset directory to custom model storage. Failed setup can be retried by activating vocabulary rescoring again.

## Boundaries

- This 110M model is **English-trained**. German speech, names and TypeWhisper brand corrections are not validated.
- Managed NeMo-style features are independently implemented; numeric CoreML/ONNX frontend parity remains unverified. Tokenization now uses the original BPE merge ranks with lowercase/NFKC and boundary variants, following FluidAudio 0.15.5. Unknown characters fail closed rather than using unknown-token evidence.
- Scoring now uses per-token CTC scores, a base context bonus of 4.5 and long-token scaling `1 + log2(tokens/3) * 0.3` above three tokens. The fork uses a default similarity threshold of 0.60 for every vocabulary size; per-term overrides remain honored. It does not implement every FluidAudio candidate/rescue heuristic. English and German real-dictation accuracy still require validation.
- At most 256 hints, 64 acoustically evaluated candidates and three-word spans per request; emission windows are bounded to 30 seconds. Missing/unalignable token timings leave the text unchanged.
- Native inference drains before unload. Cancellation rejects stale results; disabling this optional stage preserves the original dictation.

## Fork differences

- Select overlapping proposals by text similarity first, using the acoustic score margin only to break ties. This avoids swallowing neighboring words.
- Skip candidate windows that already contain the vocabulary term as a whole word.
- Use a default minimum similarity of **0.60** for every term count. Per-term `MinimumSimilarity` overrides still take precedence; the adaptive acoustic bonus is unchanged.

## Verification

`tests/TypeWhisper.PluginSystem.Tests` covers CTC repeated-label/blank transitions, bounded scoring, acoustic preference, cancellation, tokenizer normalization, asset integrity and repair, selection policy, lifecycle contracts and native ONNX Runtime coexistence. These tests require no downloaded model. Real microphone/token alignment and non-English accuracy still require validation; these checks are not performance benchmarks.
