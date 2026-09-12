# Privacy Policy

TypeWhisper does not collect telemetry or analytics by default.

## Optional crash and performance reports

Crash and performance reporting is off by default. It is enabled only via Advanced → Data → "Send anonymous crash and performance reports". Reports go to Sentry (sentry.io, US region), operated by Excel on the Web.

When enabled, reports include:

- App version and install kind.
- OS name/version and CPU architecture, memory size, .NET runtime version, and GPU vendor/model.
- Engine and model names.
- Error type, stack trace (file names and line numbers of TypeWhisper's own code), HTTP status or socket error codes where applicable. Error message text is never sent.
- Timings of dictation stages and app startup.
- Audio length in seconds and transcript character count.

Reports never include:

- Audio, transcripts, prompts, or clipboard contents.
- Window titles or URLs, or target app names.
- API keys or tokens.
- Usernames, home paths, machine name, IP address, or e-mail addresses.
- Any persistent install or user identifier.

No data is written to disk for this feature. Turning the switch off stops reporting immediately.

Local transcription and text processing run on the user's device.

Audio, transcripts, prompts, or API keys leave the device only when the user explicitly configures and uses a cloud provider or integration.

Local history and settings are stored on the user's machine under `~/.local/share/TypeWhisper` and can be deleted by the user. Deleting a settings file is not enough on its own: the app keeps a last-known-good copy alongside it (`settings.json.bak`) and quarantines unparsable copies as `settings.json.broken-*`, retired undecryptable provider secrets are preserved as ciphertext in `retired-provider-secrets.quarantine.json`, and settings backup archives created from the app retain their own copies. Remove those sidecars too, along with any backup archives, to leave nothing recoverable.
