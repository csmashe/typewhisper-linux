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

Local transcription and text processing run on the user's device.

Audio, transcripts, prompts, or API keys leave the device only when the user explicitly configures and uses a cloud provider or integration.

Local history and settings are stored on the user's machine under `~/.local/share/TypeWhisper` and can be deleted by the user. Deleting a settings file is not enough on its own: the app keeps a last-known-good copy alongside it (`settings.json.bak`) and quarantines unparsable copies as `settings.json.broken-*`, retired undecryptable provider secrets are preserved as ciphertext in `retired-provider-secrets.quarantine.json`, and settings backup archives created from the app retain their own copies. Remove those sidecars too, along with any backup archives, to leave nothing recoverable.

Dictation captures are stored in `Audio/dictation-*.wav` under the TypeWhisper data directory for recovery from History. The default retention is 30 days; expired captures and captures without a history record are removed at startup and shutdown. Deleting history also deletes its audio. Set “Keep dictation recordings for recovery (days)” to `-1` to delete captures when the app closes (and at the next startup). Include these WAV files when removing local dictation data.
