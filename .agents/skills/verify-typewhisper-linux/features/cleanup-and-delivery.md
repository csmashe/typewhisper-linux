# Cleanup and text delivery

## Sub-features

- Rule cleanup steps run on every dictation: spoken commands, spoken punctuation, number normalization, formatting, cleanup, snippets, and dictionary corrections.
- Medium and High cleanup send the text to the selected LLM provider. On failure or a missing provider they fall back to Light cleanup.
- Live cleanup prepares a draft while the user speaks. Final cleanup reuses it when it still matches.
- Delivery copies, pastes, or types the final text into the focused app.
- `POST /v1/text/process` runs raw text through the same final pipeline without inserting it, for quality evals.

## How to get to it (user POV)

Settings, then Dictation: choose Auto cleanup and Clean drafts while recording. Settings, then Prompts: choose the LLM for cleanup. Dictate with the hotkey; the cleaned text appears in the focused app. Text insertion behavior is under Settings, then Text insertion.

## Driving it with an isolated instance

End to end, with a fake LLM and clipboard read-back:

```bash
just build-release
just live-smoke artifacts/verification/$(date +%F)-cleanup
```

Passing checks in the printed summary: `text_ready`, `delivered_to_clipboard` (`delivery_outcome` is `CopiedToClipboard`), `clipboard_read_back_matches`, `llm_route_correct`, and `live_cleanup_ran_before_stop`. The `performance` block reports `transcription_milliseconds`, per-step `processing_milliseconds`, `delivery_milliseconds`, `stop_to_delivery_milliseconds`, `live_cleanup_requests`, and `live_cleanup_reused`. Proven on 2026-10-14: two consecutive runs passed every check, with stop-to-delivery near 810 ms, of which clipboard delivery took about 350 ms.

Text cleanup over the eval cases, then the eval thread's review:

```bash
just eval-capture artifacts/evals/$(date +%F)-capture/results.jsonl
just eval-review artifacts/evals/$(date +%F)-capture/results.jsonl artifacts/evals/$(date +%F)-capture/review
```

Proven on 2026-10-14: 30 of 30 cases completed with 30 LLM requests, and the review produced `review.html`.

## Gotchas

- `state: ready` means text is ready, not delivered. A run once reported `ready` with `delivery_outcome: Failed`.
- At Medium or High, a missing or failing provider returns Light text with no error, and the result still says Medium. The only signal is that no `Cleanup` call appears in `llm_calls`. Right after startup the provider registers about one second after the API answers; `capture_text_evals.py` waits for a canary to reach the LLM first.
- Live cleanup waits 2 s after the last transcript change (`LiveCleanupSession` debounce). A clip followed by less silence never sends a draft; `live_smoke.py` records 4 s of silence by default.
- The fake LLM echoes its input. It proves request routing, timing, and reuse, not writing quality. Real-provider runs need that provider's credentials inside the isolated instance; decide that with the user.
- The OpenAI-compatible plugin used to append `/v1/chat/completions` to a base URL that already ended in `/v1`, producing `/v1/v1/...`; it now drops the repeated segment. The fake LLM still rejects any unexpected path.
- Clipboard delivery on Wayland uses `wl-copy` and writes the user's real clipboard. `live_smoke.py` saves plain-text clipboards first and restores them, and refuses to run with non-text clipboard content. `--desktop hidden` delivers into a hidden desktop's clipboard instead; it is not proven live yet.
- The app refuses to start dictation while the user's session is locked (`[Dictation] Start rejected: session locked or inactive.`, HTTP 409 from `/v1/dictation/start`). `live_smoke.py` checks the lock first and stops with a clear message. `wl-paste` on GNOME can also block while locked; every clipboard call has a 5 s deadline.
- `/v1/text/process` skips only audio-token vocabulary rescoring. It does not prove capture, streaming, or delivery.
