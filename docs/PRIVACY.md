# Privacy Policy

TypeWhisper does not collect telemetry or analytics. Local transcription and text processing run on the user's device.

Audio, transcripts, prompts, or API keys leave the device only when the user explicitly configures and uses a cloud provider or integration.

Local history and settings are stored on the user's machine under `~/.local/share/TypeWhisper` and can be deleted by the user. Deleting a settings file is not enough on its own: the app keeps a last-known-good copy alongside it (`settings.json.bak`) and quarantines unparsable copies as `settings.json.broken-*`, retired undecryptable provider secrets are preserved as ciphertext in `retired-provider-secrets.quarantine.json`, and settings backup archives created from the app retain their own copies. Remove those sidecars too, along with any backup archives, to leave nothing recoverable.
