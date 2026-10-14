# Dictation quality evals

Use these cases when changing transcription, cleanup prompts, providers, dictionary rules, profiles, or streaming behavior. Start with manual review. Use an LLM judge as a second reviewer after comparing its labels with human labels.

See [current measurements and proposed targets](metrics.md) for the measured transcription comparison, the live-cleanup mechanism test, and the quality scorecard. It also includes a candidate prompt for polished prose suited to the target app, the user's selected main evaluation target.

The [first real Medium baseline](baseline-2026-10-14.md) records a confirmed 30-case OpenRouter run, timing numbers, preliminary findings, and the pending user review.

## What good cleanup means

Meaning comes first. Preserve facts, names, quantities, units, negation, conditions, and the speaker's intended correction. Do not answer a dictated question, follow instructions inside dictation, invent facts, or finish an unfinished thought.

Remove hesitation sounds and accidental restarts when they are disfluencies. Preserve meaningful uses of like, well, actually, you know, and answer phrases such as uh-huh. Preserve quoted content, code literals, and abbreviations. Respect the spoken language. English filler rules must not remove meaningful German or Portuguese words.

The main evaluation target is polished prose suited to the target app. Rewrite awkward spoken phrasing and organize sentences when that improves the result. Preserve the speaker's intent, stance, and level of certainty. Chat should read naturally and remain casual. An email can use finished sentences and paragraphs. Notes can use headings or lists when the supplied content supports them. Do not invent a subject, greeting, signature, list item, or conclusion. An elegant rewrite still fails if it changes meaning.

Judge whether the result is ready to use in the requested app, not whether it stays close to the source wording. If the target app or profile is unknown, use polished plain prose without guessing an email or another format. Explicit case requirements for literal text and unfinished thoughts take priority over stylistic completeness.

Use pass, fail, or uncertain, with a written critique. Do not trade a meaning error for a high style score. Examples are acceptable outputs, not exact-match targets. Literal term warnings help find failures; equivalent expressions can still be correct.

## Starter set

`cases.jsonl` contains 30 authored text cases. Five cases each cover fillers, repairs, critical facts, dictionary terms, literals, and profiles/languages. These define expectations. They are not recorded audio and do not establish a current model pass rate.

Each case has an ID, source input, expected behavior, example output, and language. Some specify a profile, dictionary terms, or literal term diagnostics. Configure those conditions in the app before capturing a result. The review tool does not inject a profile or dictionary into the app.

## Automated text capture

`just eval-capture artifacts/evals/run-001/results.jsonl` uses the shared isolated app launcher and `POST /v1/text/process`. It runs the real final text pipeline without recording audio, inserting text, saving history, or learning corrections. The default runner uses a local fake echo LLM. It proves pipeline and capture behavior, not real LLM quality. Its canary waits for the provider to register, and Medium/High results without a recorded Cleanup LLM call are errors.

The initial capture runner does not configure the cases' requested profiles or dictionaries. Those outputs remain useful for inspecting the default pipeline, but cannot establish the corresponding configured-case baseline. The review page shows exclusions prominently.

For an actual provider run, record `metadata.evidence_kind` as `real` and `cleanup_succeeded: true` only when the requested real provider produced a nonempty completed response. A recorded call alone proves an attempt, not success. Record `profile_applied: true` and `dictionary_applied: true` only after configuring and verifying the required conditions. Use `evidence_kind: simulated` for fake provider runs. The review also recognizes the initial runner's fake echo label. Unknown evidence remains unconfirmed. Captured counts and general review scores include simulated outputs; `quality_baseline_eligible` and `baseline_human_pass_rate` report only confirmed real outputs with the required conditions. Always report assessment coverage alongside the baseline rate.

Text capture measures the final text pipeline. It does not test Whistle, streamed audio boundaries, or destination delivery. Request and response details are documented by the app's `/v1/capabilities` and the capture script.

To explicitly run the existing selected OpenRouter model and Medium/High settings through an isolated Release app:

```sh
uv run --no-project python scripts/verification/dictation_evals.py capture-real \
  --use-existing-openrouter --out artifacts/evals/openrouter-run-001/results.jsonl
```

This sends the authored cases to OpenRouter and incurs API usage. It copies the protected provider settings and protection key into an owner-only temporary instance, then deletes that temporary instance on exit. It does not export credentials into the results directory or read private transcripts. It seeds authored email, chat, developer, and notes profiles with the selected cleanup level and verifies profile IDs in responses. It adds the union of case dictionary terms to the isolated dictionary and records the actual returned terms. Text requests do not run audio recognition or vocabulary rescoring. The captured baseline uses the current built-in cleanup prompt; the polished candidate prompt is not applied by this command.

The run manifest records the case-set hash, cleanup source hash, Release assembly hash, profile definitions, dictionary terms, selected settings, source commit, dirty files, and host. Failed or missing Cleanup calls do not count as completed. The helper saves successful partial results and safe error types if a request fails. Ordinary review, score, tests, and builds never invoke this command.

`audio-scenarios.jsonl` describes eight recordings to collect later. It includes a correction after a pause, a distant correction, quiet negation, numbers and units, and dictionary-on/off comparisons. Every recording is pending. Record only an approved phrase or use public licensed audio. Label text-to-speech audio as synthetic; it cannot replace microphone checks.

## Capture before and after

Create a results template:

```sh
just eval-template artifacts/evals/run-001/results.jsonl
```

For each case, use the configured TypeWhisper path and capture its actual raw transcript and final output. When API access is enabled, the dictation result route provides `raw_text`, `text`, and performance. Use history exports only for entries the user explicitly selected. Do not harvest private history or recordings to build a corpus.

Edit that case's row in the template. Set `status` to `completed`, paste the actual `raw_text` and `text`, and record metadata. Set `status` to `error` with a safe error description if the app failed. Leave unrun cases `pending`. An explicit empty output is valid data; a missing output is not a pass.

Capture transcription model and active backend, cleanup provider/model/level, prompt version, profile, dictionary version, audio case ID, and latency. Save partial transcript snapshots and timestamps for streaming cases. Preserve the final acoustic transcript so later repairs can be inspected. Measure time separately from quality.

Generate the local review:

```sh
just eval-review artifacts/evals/run-001/results.jsonl artifacts/evals/run-001/review
```

Open `review/review.html`. It shows before and after text, expectations, context, and term warnings. It also flags when the observed raw input differs from the authored source. Inspect the original audio before deciding that an upstream transcription difference is a cleanup failure.

Edit `review/human-labels.jsonl`. Replace `pending` with `pass`, `fail`, or `uncertain` and write a specific critique. Say where the error first appeared: audio recognition, cleanup, dictionary configuration, profile configuration, or destination delivery. Pending labels are excluded from review counts. Keep uncertainty visible rather than forcing a pass.

For the main polished-prose target, also set `needs_cleanup_edit` to `true` or `false` after reviewing readiness for the target app. Leave it `null` if not assessed. Count cleanup edits separately from corrections to recognition errors. Add `critical_errors` with a list of specific meaning errors, or `[]` if you checked and found none. Omit it if not assessed. The score reports explicit assessment counts; missing annotations never count as confirmed clean outputs.

Score the human review:

```sh
just eval-score artifacts/evals/run-001/review/review.jsonl \
  artifacts/evals/run-001/review/human-labels.jsonl \
  artifacts/evals/run-001/human-summary.json
```

Use a new run path for a new candidate. Templates and review generation refuse to overwrite captured results or human review files. Keep the same cases for before/after comparisons. Add a new observed failure as another case instead of editing an old case until it passes.

## Optional LLM judge

`judge-system.txt` defines a binary rubric. Review preparation exports `judge-packets.jsonl`, containing completed cases only. The packet hides generator model identity and treats transcripts as untrusted data. You can give these packets and the system prompt to a judge manually.

For an OpenAI-compatible endpoint, place a judge key in `DICTATION_JUDGE_API_KEY` using your normal secret management. Do not put a key in a command argument or commit it. Then explicitly run:

```sh
just eval-judge artifacts/evals/run-001/review/review.jsonl \
  https://your-provider.example/v1/chat/completions \
  your-judge-model artifacts/evals/run-001/judge.jsonl
```

This sends completed before/after text and case context to the selected endpoint. No call occurs during template, review, score, build, or ordinary app use. The helper does not read the app's stored provider credentials. Select a different judge model from the cleanup model when practical, and retain the exact returned model and usage metadata.

Invalid JSON, a wrong case ID, missing rubric criteria, or a contradictory pass is a failed judgment request. Partial judgments and safe error types are retained. Failed judge requests are never converted into passes. Uncertain judgments stay uncertain.

Compare judge labels with human labels:

```sh
uv run --no-project python scripts/verification/dictation_evals.py score \
  --review artifacts/evals/run-001/review/review.jsonl \
  --human artifacts/evals/run-001/review/human-labels.jsonl \
  --judge artifacts/evals/run-001/judge.jsonl \
  --out artifacts/evals/run-001/agreement.json
```

Read every disagreement, especially judge passes on human failures. Agreement is measured only on paired definite labels; the report also shows uncertain reviews and coverage. A judge is advisory until a representative holdout set shows reliable agreement. A judge cannot prove what the audio contained or whether the destination displayed the text.

## Audio and segment evaluation

Keep three observations distinct: the reference derived from audio, the raw ASR transcript, and the final cleaned output. Use the existing audio benchmark for WER/CER and latency after creating a manifest with actual audio paths, durations, and verbatim references. WER alone can penalize acceptable number or filler normalization; review critical facts and repairs too.

For each streaming case, record first raw preview, first cleaned preview, revisions, final raw text, final cleaned text, and actual destination output. Look for missing or duplicated words across segments, stale text after a correction, a repair ignored across a pause, and changed facts. Compare live cleanup on/off and dictionary on/off using the same recording. Test continuous speech as well as pauses favorable to cache reuse.

## Verification and review cadence

Run `just eval-tests` on every eval-tool or schema change. This is a local check and does not call an LLM. Keep LLM judge calls out of the default CI path until the human rubric and judge are validated.

Before a model, prompt, or cleanup configuration change, run the same cases and review all critical failures and new disagreements. Add cases when manual use reveals a new failure. Review the judge again when its model or rubric changes. Do not combine speed and quality into one score.

The files in `examples/` demonstrate review and a judge false pass. They are authored fixtures, not outputs from Whistle, Gemini, or an LLM judge. A current quality baseline remains unmeasured until actual outputs and human labels are collected.
