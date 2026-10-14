# App startup and dictation control

## Sub-features

- The Linux app starts in the desktop session and exposes its process state.
- The control command reports status without starting or stopping a recording.
- The user can start and stop dictation from a configured shortcut or the `typewhisper` command.
- The app returns to idle after dictation ends.

## How to get to it (user POV)

Start TypeWhisper from the desktop launcher or tray. Press the configured dictation shortcut, speak a short approved phrase, then press the shortcut again. The CLI route uses `typewhisper record start` and `typewhisper record stop`.

## Driving it with the Linux control CLI

Run `typewhisper status` before recording. Confirm the response has `ok: true` and `state: idle`. For a microphone check, start a recording, speak a fixed phrase, stop it, and run `typewhisper status` again. Confirm the app is idle and that a completed transcription appears in History. Do not run this microphone path without permission to capture the phrase.

The read-only status check used to prove this feature on 2026-10-13 returned `ok: true`, `state: idle`, backend `linux-evdev`, and the active binding `Ctrl+Shift+Space`. It created no recording or history entry.

## Gotchas

- A second app instance can share the same control socket. Check the doctor before launch.
- `typewhisper record stop` can create a private audio file and a history entry.
- A hotkey backend can be unavailable in a nested or remote desktop session. Report that as an environment limit.
- Stop only the process started for the check. Do not terminate the user's existing app.
