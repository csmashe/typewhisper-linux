# App startup, dictation control, and shortcuts

## Sub-features

- The Linux app starts in the desktop session and exposes its process state.
- The control command reports status without starting or stopping a recording.
- The user can start and stop dictation from a configured shortcut or the `typewhisper` command.
- Settings, then Shortcuts records a new main dictation hotkey with Record keys and saves it with Apply.
- The app returns to idle after dictation ends.

## How to get to it (user POV)

Start TypeWhisper from the desktop launcher or tray. Press the configured dictation shortcut, speak a short approved phrase, then press the shortcut again. The CLI route uses `typewhisper record start` and `typewhisper record stop`. To change the shortcut, open Settings, then Shortcuts, click Record keys under Main dictation hotkey, press the new combination, and click Apply.

## Driving it with the control CLI, an isolated instance, and the hidden desktop

Run `typewhisper status` before recording. Confirm the response has `ok: true` and `state: idle`. For a microphone check, start a recording, speak a fixed phrase, stop it, and run `typewhisper status` again. Confirm the app is idle and that a completed transcription appears in History. Do not run this microphone path without permission to capture the phrase.

The read-only status check returned `ok: true`, `state: idle`, backend `linux-evdev`, and the user's active binding on 2026-10-13 and 2026-10-14. Bindings change with user settings, so check the shape, not the value.

For a scripted dictation without a physical microphone, use `just live-smoke`. It starts an isolated instance, routes its capture to a null sink, confirms through `pactl` that the app records only from that sink, plays a public clip, and stops through the API.

For shortcut recording, use `just ui-shortcuts`:

```bash
just build-release
just ui-shortcuts artifacts/verification/$(date +%F)-shortcuts Ctrl+Shift+F8
```

It opens the isolated app in the hidden desktop, clicks the Shortcuts nav item and Record keys through AT-SPI, sends the key combination through mutter's RemoteDesktop API, clicks Apply, and reads `toggleHotkey` from the instance's `settings.json`. The check passes when the saved value equals the requested combination. Screenshots of each step are saved beside `shortcuts.json`. Proven on 2026-10-14: `Ctrl+Shift+F8` was recorded and saved.

## Gotchas

- A second app instance started with the user's environment shares the user's control socket and data. Use `isolated_app.py`, which moves every state location, instead of launching the build directly.
- The app replaces an empty main hotkey with `Ctrl+Shift+Space` at startup. In an isolated instance the evdev backend stays off, and the SharpHook backend reports capture scope "Focused only (TypeWhisper window)", so the instance reacts only while its own window has focus.
- The runtime folder must stay short. The API socket path is limited to 108 bytes, and a failed bind stops the TCP API too. The failure is logged as `[HttpApiService] Local API failed to start` and shown in Settings.
- Typing into the hotkey box does not record a shortcut. The recorder starts only after Record keys, and the value is saved only after Apply.
- The hotkey box's text is not readable over AT-SPI; check the saved setting, not the box.
- Never send keys with `ydotool` or other uinput tools; they type into the user's real desktop. The hidden desktop's RemoteDesktop input reaches only the hidden compositor.
- `typewhisper record stop` can create a private audio file and a history entry.
- A hotkey backend can be unavailable in a nested or remote desktop session. Report that as an environment limit.
- Stop only the process started for the check. Do not terminate the user's existing app.
