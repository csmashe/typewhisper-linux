# Phase 5 — Full CLI Subcommands

Status: blocked on Phase 4
Depends on: Phase 4 (control socket, single-instance, settings UX)
Unblocks: Phase 6 (DE helpers can wire to specific subcommands)

## Goal

Expand the minimal `typewhisper` (toggle) entry point into the full subcommand surface so Hyprland / Sway / Niri compositor bindings can drive true push-to-talk via separate press and release binds:

```bash
typewhisper                       # toggle (from Phase 4 — unchanged)
typewhisper record start          # idempotent: no-op if already recording
typewhisper record stop           # idempotent: no-op if already idle
typewhisper record toggle         # same as bare `typewhisper`
typewhisper record cancel         # drop in-flight audio, no transcription
typewhisper status                # JSON output with current state
```

These commands enable:

```ini
# Hyprland — true PTT without joining `input` group
bind  = CTRL SHIFT, SPACE, exec, typewhisper record start
bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop
bind  = CTRL SHIFT, ESCAPE, exec, typewhisper record cancel
```

## Scope

In scope:

- Subcommand parser (`record start|stop|toggle|cancel`, `status`).
- JSON line protocol upgrade for the control socket: each request is one JSON line, each response is one JSON line.
- Idempotency: all commands safe to call repeatedly; in-flight state machine in the orchestrator handles dedup. Already done in Phase 2 — Phase 5 surfaces it through CLI.
- `status` returns current state (`idle`, `recording`, `transcribing`, `injecting`), active backend, and active hotkey binding.
- Backwards compatibility with Phase 4's bare `typewhisper` toggle behavior — still works, still uses the same socket.

Out of scope:

- D-Bus service. Still not needed. The socket carries everything.
- Per-recording profile or prompt selection through CLI flags (e.g., `--profile foo`). Possible follow-up but not blocking compositor binds.
- DE-specific writer scripts (`gsettings`, `hyprctl`). Phase 6.

## Files

### New

```
src/TypeWhisper.Linux/Cli/CommandLineParser.cs
src/TypeWhisper.Linux/Cli/Commands/RecordCommand.cs
src/TypeWhisper.Linux/Cli/Commands/StatusCommand.cs
src/TypeWhisper.Linux/Services/Ipc/JsonControlProtocol.cs
```

### Modified

```
src/TypeWhisper.Linux/Program.cs                            # subcommand routing
src/TypeWhisper.Linux/Services/Ipc/ControlSocketServer.cs   # JSON protocol; new verbs
src/TypeWhisper.Linux/Services/Ipc/ControlSocketClient.cs   # JSON send/receive helpers
src/TypeWhisper.Linux/Views/Settings/ShortcutsSettingsView.axaml  # show Hyprland/Sway snippets per desktop
```

## Protocol

JSON line over Unix socket. One request per connection (caller closes after response).

Request:

```json
{"v":1,"cmd":"record.start"}
{"v":1,"cmd":"record.stop"}
{"v":1,"cmd":"record.toggle"}
{"v":1,"cmd":"record.cancel"}
{"v":1,"cmd":"status"}
```

Response for action commands:

```json
{"v":1,"ok":true,"state":"recording","prev":"idle"}
```

Response for `status`:

```json
{
  "v": 1,
  "ok": true,
  "state": "recording",
  "backend": "linux-evdev",
  "supports_press_release": true,
  "active_binding": "RightCtrl",
  "mode": "PushToTalk"
}
```

Error:

```json
{"v":1,"ok":false,"error":"unknown-command"}
```

The `v: 1` version field is for future protocol upgrades. Don't break it.

### Backwards compatibility with Phase 4

Phase 4 used a plain `toggle\n` text line. Phase 5's server accepts both:

- If the line starts with `{`, parse as JSON.
- Otherwise treat as the legacy `toggle` line (still respond `ok\n`).

This lets a Phase-4-built CLI binary talk to a Phase-5 running app and vice versa during upgrade windows.

## Command implementations

Each command is a thin client that builds a JSON request, sends it, prints the response.

```csharp
internal static class RecordCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) return PrintUsage();
        var verb = args[0] switch
        {
            "start"  => "record.start",
            "stop"   => "record.stop",
            "toggle" => "record.toggle",
            "cancel" => "record.cancel",
            _        => null,
        };
        if (verb is null) return PrintUsage();

        var path = SocketPathResolver.ResolveControlSocketPath();
        if (!ControlSocketClient.TrySendJson(path, new { v = 1, cmd = verb }, out var resp, out var err))
        {
            Console.Error.WriteLine($"typewhisper: {err ?? "not running"}");
            return 2;
        }
        Console.WriteLine(resp);
        return resp.Contains("\"ok\":true") ? 0 : 1;
    }
}
```

The server side wires verbs to `DictationOrchestrator`:

| Verb | Orchestrator call |
|---|---|
| `record.start` | `StartDictationAsync()` — idempotent |
| `record.stop` | `StopDictationAsync()` — idempotent |
| `record.toggle` | `ToggleDictationAsync()` |
| `record.cancel` | `CancelDictationAsync()` — drops audio, no transcription |
| `status` | snapshot of `DictationOrchestrator.CurrentState` + `HotkeyService` info |

### Idempotency contract (already established in Phase 2)

| Current state | Verb | Behavior |
|---|---|---|
| idle | record.start | enter recording |
| recording | record.start | no-op, ok |
| recording | record.stop | enter transcribing |
| idle | record.stop | no-op, ok |
| transcribing | record.stop | no-op, ok (transcription continues) |
| recording | record.cancel | drop audio, return to idle |
| transcribing | record.cancel | abort transcription if safe, suppress output |
| idle | record.cancel | no-op, ok |
| any | record.toggle | start if idle, stop otherwise |

## Settings panel update

In Settings → Shortcuts → "Bind a custom shortcut", show desktop-aware snippets:

- **Hyprland detected** → show `bind` + `bindr` snippet with `record start` / `record stop`. Mark "supports push-to-talk".
- **Sway detected** → show `bindsym --no-repeat` / `bindsym --release` pair. Mark "supports push-to-talk".
- **GNOME / KDE / unknown** → show the single `typewhisper` command (Phase 4 behavior). Mark "toggle only".

Each snippet has a copy button. Don't auto-write yet — that's Phase 6.

## Exit criteria

- `typewhisper record start` and `typewhisper record stop` toggle the recording state in a running instance, idempotently. Hyprland `bind` + `bindr` reliably starts and stops dictation regardless of focused window.
- `typewhisper status` prints valid JSON with current state, backend, binding, and mode.
- `typewhisper record cancel` drops in-flight audio with no transcription side effects.
- Phase 4's bare `typewhisper` still works (legacy + JSON paths coexist).
- Running each command when no instance is alive returns a non-zero exit code with a clear message, not a stack trace.
- A user on Hyprland can bind `bind` + `bindr` lines and get Hybrid mode without joining the `input` group.

### Manual stress tests

| Test | Pass condition |
|---|---|
| 100 rapid alternations of `record start` and `record stop` via shell loop | State settles. No leaks, no stuck transcribing. |
| `record start` while another `record start` is in flight (race) | Second one no-ops cleanly. |
| `record cancel` mid-transcription | Output suppressed, state returns to idle within reasonable time. |
| `record stop` immediately after `record start` (e.g., key tap < 50ms) | Either: a) recording captured silence and ran transcription, b) cancel-on-too-short triggers per existing TypeWhisper behavior. Either is acceptable; document which. |
| Run the CLI from a sandboxed shell (Flatpak) | Either works (if socket is shared) or fails with a clear message — no hang. |

## What this phase explicitly does NOT do

- Does NOT add `--profile` or other per-call configuration flags. Keep the surface minimal until users ask.
- Does NOT add D-Bus. If desktop integrations later need D-Bus method names, add it then.
- Does NOT auto-create compositor config files. Phase 6.
- Does NOT add a `daemon` subcommand. TypeWhisper is launched as a UI app; the socket server starts as part of the app's hosted services. There is no separate daemon process.

## Risks

| Risk | Mitigation |
|---|---|
| JSON protocol mistakes (unbounded reads, oversized payloads). | Cap line length at 4 KB. Reject larger. |
| Compositor binds fire rapidly enough to cause queue depth in the socket. | Accept up to 8 simultaneous connections (already set in Phase 4). Each request is tiny and stateless. |
| Hyprland `bindr` fires the release before the `bind`'s exec finishes spawning. | The orchestrator is idempotent — a stop arriving before start completed will no-op; the start will then run with no matching stop and produce a stuck recording. Mitigation: in `record.start`, if a `record.stop` arrives within 100ms while start is still in flight, treat the start as a tap and stop immediately. Worth a small synchronization guard in the orchestrator. |
| User binds the same key combo to both compositor and evdev. | Both backends will fire; the orchestrator's idempotency makes this safe but suboptimal. Status panel should warn: "Multiple shortcut backends are active for the same key. Disable one." |
| Versioning breakage on `v: 1`. | Server rejects unknown versions with `{"ok":false,"error":"unsupported-version"}`. Bump cautiously. |
