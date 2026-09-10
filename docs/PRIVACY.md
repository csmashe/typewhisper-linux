# Privacy Policy

TypeWhisper does not collect telemetry or analytics. Local transcription and text processing run on the user's device.

Audio, transcripts, prompts, or API keys leave the device only when the user explicitly configures and uses a cloud provider or integration.

Local history and settings are stored on the user's machine under `~/.local/share/TypeWhisper` and can be deleted by the user. Deleting a settings file is not enough on its own: the app keeps a last-known-good copy alongside it (`settings.json.bak`) and quarantines unparsable copies as `settings.json.broken-*`, retired undecryptable provider secrets are preserved as ciphertext in `retired-provider-secrets.quarantine.json`, and settings backup archives created from the app retain their own copies. Remove those sidecars too, along with any backup archives, to leave nothing recoverable.

Dictation captures are stored in `Audio/dictation-*.wav` under the TypeWhisper data directory for recovery from History. The default retention is 30 days; expired captures and captures without a history record are removed at startup and shutdown. Deleting history also deletes its audio. Set “Keep dictation recordings for recovery (days)” to `-1` to delete captures when the app closes (and at the next startup). Include these WAV files when removing local dictation data.
