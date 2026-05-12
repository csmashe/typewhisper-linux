# Phase 4 — Single-Instance + Minimal CLI

Status: blocked on Phase 3
Depends on: Phase 3 (settings UI, capability flags)
Unblocks: Phase 5 (full CLI), Phase 6 (DE helpers that bind to commands)

## Goal

Unblock the GNOME/KDE custom-shortcut path *without* building a full subcommand surface. Specifically:

- A bare `typewhisper` invocation, when an instance is already running, sends "toggle dictation" to the existing instance and exits.
- Single-instance enforcement falls out for free — launching TypeWhisper twice no longer runs two copies; the second invocation refocuses or toggles the first.

This phase deliberately does **not** add `record start` / `record stop` / etc. — those are Phase 5 because they're only needed for Hyprland/Sway press+release binds.

## Scope

In scope:

- Unix domain socket at `$XDG_RUNTIME_DIR/typewhisper/control.sock`.
- Fallback path `/tmp/typewhisper-$UID/control.sock` with `0700` directory permission when `XDG_RUNTIME_DIR` is unset.
- On startup: try to bind the socket. If already bound by a live peer, this process is a second invocation.
- Second-invocation behavior: send a `toggle` message over the socket, then exit 0.
- Stale socket detection: if the socket file exists but `connect()` fails with `ECONNREFUSED`, remove the file and bind fresh.
- IPC server inside the running app: minimal — accept a connection, read one line ("toggle"), call `DictationOrchestrator.ToggleDictationAsync`, write "ok\n", close.
- Settings → Shortcuts: add a "Bind a custom shortcut" helper section that copies the right command for the user's desktop.

Out of scope:

- Multi-command surface (`record start`, etc.) — Phase 5.
- JSON protocol — Phase 5 introduces it. Phase 4 uses raw text line ("toggle\n") because that's all we need.
- D-Bus service — not in this phase. The Unix socket is simpler and sufficient.
- DE-specific shortcut auto-creation — Phase 6.

## Files

### New

```
src/TypeWhisper.Linux/Services/Ipc/ControlSocketServer.cs
src/TypeWhisper.Linux/Services/Ipc/ControlSocketClient.cs
src/TypeWhisper.Linux/Services/Ipc/SocketPathResolver.cs
src/TypeWhisper.Linux/CommandLineEntry.cs                     # bare-arg handling
```

### Modified

```
src/TypeWhisper.Linux/Program.cs                              # entry point routing
src/TypeWhisper.Linux/ServiceRegistrations.cs                 # register ControlSocketServer
src/TypeWhisper.Linux/App.axaml.cs                            # start socket server during init
src/TypeWhisper.Linux/Views/Settings/ShortcutsSettingsView.axaml  # custom-shortcut helper
```

## Implementation notes

### Socket path

```csharp
public static string ResolveControlSocketPath()
{
    var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    if (!string.IsNullOrEmpty(xdg))
    {
        var dir = Path.Combine(xdg, "typewhisper");
        Directory.CreateDirectory(dir);              // honors umask 077 in XDG_RUNTIME_DIR
        return Path.Combine(dir, "control.sock");
    }
    var uid = (int)Mono.Unix.Native.Syscall.getuid();   // or P/Invoke geteuid()
    var fallback = $"/tmp/typewhisper-{uid}";
    Directory.CreateDirectory(fallback);
    Chmod(fallback, 0700);
    return Path.Combine(fallback, "control.sock");
}
```

Note: `Directory.CreateDirectory` doesn't set mode bits on existing directories. For the `/tmp` fallback, explicitly `chmod` after create-or-noop.

### Startup routing

`Program.Main`:

```csharp
public static int Main(string[] args)
{
    // Fast path: no UI arg means we may be a second invocation acting as a CLI.
    if (args.Length == 0)
    {
        var path = SocketPathResolver.ResolveControlSocketPath();
        if (ControlSocketClient.TrySendToggle(path, out var error))
            return 0;                              // we are a CLI; the running app got the message
        // Otherwise: no instance running. Fall through and start the app normally.
    }

    return RunGui(args);
}
```

`TrySendToggle`:

```csharp
public static bool TrySendToggle(string path, out string? error)
{
    error = null;
    if (!File.Exists(path)) return false;
    try
    {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        sock.Connect(new UnixDomainSocketEndPoint(path));
        sock.SendTimeout = 2000;
        sock.ReceiveTimeout = 2000;
        var msg = "toggle\n"u8;
        sock.Send(msg);
        var buf = new byte[16];
        var n = sock.Receive(buf);
        return n > 0 && Encoding.UTF8.GetString(buf, 0, n).StartsWith("ok");
    }
    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
    {
        // Stale socket. Caller should clean it up and start fresh.
        try { File.Delete(path); } catch { }
        return false;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}
```

### Server inside the running app

`ControlSocketServer` is an `IHostedService`-style component started during `App.OnFrameworkInitializationCompleted`:

```csharp
internal sealed class ControlSocketServer : IAsyncDisposable
{
    private readonly DictationOrchestrator _orchestrator;
    private readonly string _path;
    private Socket? _listener;
    private CancellationTokenSource? _cts;

    public ControlSocketServer(DictationOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        _path = SocketPathResolver.ResolveControlSocketPath();
    }

    public void Start()
    {
        TryRemoveStaleSocket(_path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        Chmod(_path, 0600);
        _listener.Listen(8);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await _listener!.AcceptAsync(ct);
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync(ct);
            if (line == "toggle")
            {
                await _orchestrator.ToggleDictationAsync();
                await writer.WriteLineAsync("ok");
            }
            else
            {
                await writer.WriteLineAsync("err unknown-command");
            }
        }
        catch { /* swallow per-client errors */ }
    }
}
```

`TryRemoveStaleSocket`: if the path exists, attempt a `connect()`; if `ECONNREFUSED` or the file is older than process boot, delete and continue. Don't delete a path that has a live peer — that would corrupt another running instance.

### Settings panel addition

In Settings → Shortcuts, below the status panel from Phase 3, add a "Bind a custom shortcut" section:

```
┌─ Bind a custom shortcut ────────────────────────────────┐
│                                                          │
│  You can bind any key to toggle dictation through your   │
│  desktop's keyboard settings. Use this command:          │
│                                                          │
│  ┌────────────────────────────────────────────────┐ [📋]│
│  │ typewhisper                                    │     │
│  └────────────────────────────────────────────────┘     │
│                                                          │
│  Detected desktop: GNOME                                 │
│  Open Settings → Keyboard → Customize Shortcuts          │
│  → Add a new shortcut → paste the command above.         │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

Detect desktop from `XDG_CURRENT_DESKTOP` and show the right instructions per environment. Don't auto-write the custom shortcut yet (Phase 6).

## Exit criteria

- Launching `typewhisper` while no instance is running starts the app normally.
- Launching `typewhisper` while an instance is running toggles dictation in the existing instance and exits 0 within ~200ms.
- Killing the app with `kill -9` and relaunching cleans up the stale socket without manual intervention.
- A GNOME user can:
  1. Open System Settings → Keyboard → Customize Shortcuts.
  2. Add a new entry with command `typewhisper` and shortcut `Ctrl+Shift+Space`.
  3. Press `Ctrl+Shift+Space` from any focused app → dictation toggles.
- Two instances cannot run simultaneously. Second instance silently delegates to the first.
- Permissions on the socket directory and file are user-only (`0700` / `0600`). Verifiable with `ls -la $XDG_RUNTIME_DIR/typewhisper/`.

## What this phase explicitly does NOT do

- Does NOT add `record start` / `record stop`. The minimal toggle covers GNOME/KDE custom shortcuts (which only fire on press). Phase 5 adds the press+release surface for Hyprland/Sway.
- Does NOT add D-Bus. Unix socket is enough; D-Bus is heavier and only needed if external scripts ask for it.
- Does NOT auto-create the GNOME custom shortcut. The user pastes the command themselves. Phase 6 may automate this with explicit consent.

## Risks

| Risk | Mitigation |
|---|---|
| Socket survives across crash → second invocation hits "stale socket". | `TryRemoveStaleSocket` on bind probes the existing path with `connect()`; refuse → delete and continue. |
| Two concurrent connections both call `ToggleDictationAsync`. | The orchestrator is already idempotent (Phase 2). Two simultaneous toggles cancel out — that's correct. |
| User actually wants two instances (e.g., testing). | Document `--no-single-instance` flag as an escape hatch. Don't ship in main UI. |
| `XDG_RUNTIME_DIR` exists but isn't writable (broken systemd-logind setup). | Catch the bind error, fall through to `/tmp` fallback, log a warning. |
| Socket path collision with another tool. | The directory is `typewhisper/`-prefixed; collision unlikely. |
| User launches via desktop file (already passes args) and we incorrectly treat it as a CLI. | Only enter CLI mode when `args.Length == 0`. Any flag like `--profile`, `--debug`, etc., goes through the GUI entry. |
| Sandboxed launches (Flatpak/snap) may not share `$XDG_RUNTIME_DIR`. | Document: when packaged for Flatpak, the CLI fallback won't work cross-sandbox. Users in Flatpak should use evdev or portal directly. |
