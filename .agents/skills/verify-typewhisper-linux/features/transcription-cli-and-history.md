# Transcription, CLI, and history

## Sub-features

- The CLI sends a local audio file to the running app.
- The app returns text and the selected engine and model.
- A successful dictation appears in the History view.
- The user can retrieve or search saved transcription history.

## How to get to it (user POV)

Enable the local API server in Settings when needed. Use the benchmark helper with a known audio manifest to send the files through the same local CLI path. The user can also dictate through the desktop app and open History in Settings.

## Driving it with the CLI and desktop app

Use a short, non-private WAV file with a known reference. Run `typewhisper-cli transcribe` with that audio path and the options `--language en --json --no-corrections`. Save the CLI JSON output. Confirm that the response has non-empty `text`, the expected language, and the engine and model expected for the selected setup. Compare its text with the reference using the fixed evaluation set described in `asr-quality-and-performance.md`.

For a history check, use an approved test phrase through dictation. Open History and confirm the new entry has the expected text, timestamp, engine, and model. Search for a unique word from that entry. Preserve the entry unless the user asks to remove it.

## Gotchas

- The CLI uses the local API server. A running GUI does not prove the API server is enabled.
- A complete file response measures the whole request. It does not prove streaming partials.
- Corrections can change text after recognition. Use `--no-corrections` for raw model quality comparisons.
- Keep private audio and transcript text out of committed benchmark fixtures and logs.
