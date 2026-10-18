# Privacy Policy

TypeWhisper does not collect telemetry or analytics by default.

## Optional crash and performance reports

Crash and performance reporting is off by default. It is enabled only via Advanced → Data → "Send anonymous crash and performance reports". Reports are sent to a Sentry project owned by Excel on the Web. Sentry is a hosted service of Functional Software, Inc. d/b/a Sentry (sentry.io, US region), which receives and stores the reports on Excel on the Web's behalf.

When enabled, reports include:

- App version and install kind.
- OS name/version and CPU architecture, memory size, .NET runtime version, and GPU vendor/model.
- Engine and model names, recording mode, cleanup level, whether a profile was applied (yes/no only), and the detected or configured language.
- How the text was delivered (paste, typing, clipboard, or an action) and whether delivery succeeded.
- Error type, stack trace (file names and line numbers of TypeWhisper's own code), HTTP status or socket error codes where applicable. Error message text is never sent.
- Timings of dictation stages and app startup.
- Audio length in seconds and transcript character count.

Reports never include:

- Audio, transcripts, prompts, or clipboard contents.
- Window titles or URLs, or target app names.
- API keys or tokens.
- Usernames, home paths, machine name, or e-mail addresses. Reports carry no IP-address field and the SDK's automatic IP capture is disabled; like any HTTPS service, Sentry's servers still see the connection's source address, and IP storage is turned off in the project.
- Any persistent install or user identifier.

No data is written to disk for this feature. Turning the switch off stops reporting immediately.

## Optional local diagnostic log

The local diagnostic log is off by default and is a separate choice from crash reporting. It is enabled via Advanced → Data → "Keep a local diagnostic log" and writes `Logs/diagnostics.jsonl` under the TypeWhisper data directory (readable only by your user). It is never sent anywhere; you can export it to attach to a support request.

It records event names (for example the start and end of a dictation and of its recording, transcription and insertion steps), a random ID linking the steps of one dictation, timings, the outcome of each step, and the same engine, model, mode, cleanup level, language and app/OS version values as the crash reports above, plus error types, method names from the stack and HTTP status or socket error codes. Every value is checked against a fixed list of allowed fields and formats when it is written, pruned and exported, so audio, transcripts, prompts, clipboard contents, window titles, file names or paths, API keys and error message text are never written. Entries older than the chosen retention period (1, 3, 7, 14 or 30 days; 7 by default) are deleted, the file is capped at 2 MB, and settings backups do not include it. Turning the log off or choosing "Delete log" deletes the file.

Local transcription and text processing run on the user's device.

Audio, transcripts, prompts, or API keys leave the device only when the user explicitly configures and uses a cloud provider or integration.

Local history and settings are stored on the user's machine under `~/.local/share/TypeWhisper` and can be deleted by the user. Deleting a settings file is not enough on its own: the app keeps a last-known-good copy alongside it (`settings.json.bak`) and quarantines unparsable copies as `settings.json.broken-*`, retired undecryptable provider secrets are preserved as ciphertext in `retired-provider-secrets.quarantine.json`, and settings backup archives created from the app retain their own copies. Remove those sidecars too, along with any backup archives, to leave nothing recoverable.

Dictation captures are stored in `Audio/dictation-*.wav` under the TypeWhisper data directory for recovery from History. The default retention is 30 days; expired captures and captures without a history record are removed at startup and shutdown. Deleting history also deletes its audio. Set “Keep dictation recordings for recovery (days)” to `-1` to delete captures when the app closes (and at the next startup). Include these WAV files when removing local dictation data.
