# Typewhisper Linux Wayland Push-to-Talk Implementation Guide

Recommended architecture and implementation notes for a .NET 10 app

Prepared May 12, 2026

> **Recommendation:** The best path is to stop treating the app as the owner of the global key event. On Wayland, the compositor or a trusted portal owns global input. Typewhisper should expose recording actions, then let compositor shortcuts, the Global Shortcuts portal, D-Bus, or an opt-in evdev backend trigger those actions.

# Executive summary

**Your current symptom is normal for Wayland:** Ctrl+Shift+Space works when Typewhisper is foreground because the app receives its own key events. When a text editor, browser, or document window is focused, a regular app-level key listener usually cannot see that key combination. This is a Wayland security design choice, not a .NET-specific bug. The practical solution is to move the hotkey outside the focused app and into a system-approved trigger path. [S4] [S5]

- Implement Typewhisper as a long-running daemon or single-instance app with a small CLI control surface.
- Expose idempotent actions: record start, record stop, record toggle, record cancel, and status.
- For Hyprland, Sway, River, and Niri, document compositor keybindings that call start on key press and stop on key release.
- For GNOME and KDE, prefer the XDG Global Shortcuts portal where it is supported and reliable. Fall back to desktop custom shortcuts that call record toggle.
- Offer an opt-in evdev hotkey backend for users who need true push-to-talk on desktops that do not expose release events through shortcuts or portals.
- Treat text insertion as a separate problem: use Remote Desktop portal or libei/eitype first where available, then wtype, then dotool/ydotool, then a clipboard fallback.

> **Recommendation:** For Typewhisper specifically, I would ship the daemon/CLI plus compositor binding documentation first. It fixes your exact foreground-window problem with the least risk. Then add portal support and evdev fallback as enhancements.

# What the reviewed apps teach

The projects converge on one lesson: reliable Wayland dictation requires an external trigger mechanism. Some projects use compositor keybindings, some use desktop portals, some use evdev, and some use IBus. The projects that work best for true push-to-talk separate three concerns: hotkey trigger, recording state, and text insertion.

| Project | Wayland trigger strategy | PTT quality | Typewhisper takeaway |
| --- | --- | --- | --- |
| OpenWhispr | GNOME Wayland custom shortcuts via gsettings and D-Bus; separate KDE and Hyprland handling. | Good for toggle. GNOME path does not provide true key-up push-to-talk. | Copy its desktop-specific setup helpers and documentation style. |
| Whispering / Epicenter | Tauri global shortcut plugin, with command callbacks for Pressed and Released. | Good abstraction if the backend can receive events. | Copy the internal command model: Pressed starts, Released stops. Do not rely on this alone on Wayland. |
| Whisper-Wayland / VoxCtl pattern | Daemon plus CLI commands: daemon, begin, end, cancel, status. Compositor binds press and release to CLI commands. | Excellent. | Best direct pattern for Typewhisper. |
| Voxtype | Compositor keybindings first, evdev fallback, CLI record start/stop/toggle/cancel, output backend chain. | Excellent. | Best overall Linux desktop model. |
| Speed of Sound | XDG Global Shortcuts portal and D-Bus trigger script fallback; Remote Desktop portal for typing. | Good for global triggers, mostly toggle-oriented in docs. | Copy portal-first typing and trigger architecture. |
| hyprwhspr | evdev/global shortcuts with key state tracking and release callbacks. | Excellent when permissions are granted. | Good model for opt-in evdev fallback. |
| stt2desktop | Hold Scroll Lock, release to transcribe; output through wtype on Wayland or xdotool on X11. | Good. | Good minimal PTT model. |
| waystt | Signal-driven UNIX model; compositor shortcut starts the process or sends SIGUSR1. | Toggle/on-demand. | Good for lightweight CLI action design. |
| BlahST | User binds desktop hotkeys to scripts; uses clipboard and xdotool/ydotool/wl-copy tooling. | Two-hotkey or silence-based. | Good fallback docs, not the best UX. |
| WhisperTux | evdev global shortcut plus ydotool injection. | Good. | Good examples for permissions and uinput setup. |
| linux-whisper | evdev hotkey and wtype/wl-clipboard text output. | Likely good. | Useful security messaging: track only configured combo. |
| Handy | Cross-platform Tauri app; Linux/Wayland docs point to desktop-level shortcuts and external typing tools. | Depends on desktop. | Good packaging and forkable app model. |
| Hyprvoice | Daemon/control-plane architecture with Hyprland bindings and clipboard/direct typing fallback. | Good for Hyprland. | Good Hyprland-specific setup model. |
| whispers | Local-first Wayland dictation with tap-to-toggle flow and uinput/clipboard requirements. | Toggle. | Useful for minimal Rust-style workflow. |
| IBus Speech To Text | Input method instead of fake keyboard injection. | Not normal PTT. | Best concept for insertion correctness, but a major architecture change. |
| Dikt / Dictate-style GNOME tools | GNOME/Wayland IBus integration with configurable shortcut toggle. | Toggle. | Interesting for GNOME-native users, not a general Typewhisper path. |
| Wayland Voice Typer / voic.sh | Portal, wtype, or ydotool text injection backend chain. | Continuous or toggle. | Copy backend ordering for text insertion. |

Source notes for this table are listed in the Sources section, especially the OpenWhispr hotkey documentation, Voxtype README/docs, Speed of Sound keyboard shortcut docs and FAQ, Whisper-Wayland code, waystt README, WhisperTux README, IBus Speech To Text README, linux-whisper site, and the XDG portal specifications. [A1] [A5] [A6] [A7] [A8] [A11] [A12] [A14] [A15] [S4] [S5]

# Why the current Ctrl+Shift+Space listener fails on Wayland

- On X11, applications and helper libraries can often register or grab global keys directly.
- On Wayland, a normal client is not supposed to observe arbitrary keyboard input from other focused clients. This prevents keylogging and surprise input interception.
- A normal .NET, Electron, Tauri, GTK, or Qt keydown event is scoped to the focused application window unless a compositor or portal explicitly routes a shortcut to the app.
- This means an in-app listener can work when Typewhisper is focused and fail when LibreOffice, Kate, VS Code, Firefox, or a terminal is focused.
- The fix is to make Ctrl+Shift+Space a compositor or portal shortcut that calls Typewhisper, not a raw app key listener that hopes to see every key event.

> **Caution:** Do not try to bypass Wayland by polling every key by default. That will look like a keylogger and will require sensitive permissions. Use evdev only as an explicit advanced fallback.

# Recommended architecture for Typewhisper

## Core design

Build Typewhisper around a single recording state machine. Every trigger path should call the same code, whether the request comes from the UI, tray menu, CLI, compositor keybinding, D-Bus, portal, or evdev.

- Typewhisper daemon: long-running process that owns microphone capture, transcription, output insertion, and state.
- Typewhisper CLI: small command surface that sends a request to the daemon and exits quickly.
- D-Bus service: optional but recommended for desktop integration, app activation, scripts, and GNOME/KDE custom shortcuts.
- Portal client: optional but recommended for XDG Global Shortcuts and Remote Desktop keyboard injection.
- Evdev listener: optional advanced backend for users who explicitly grant input-device read permissions.

Recommended CLI surface:
```bash
typewhisper daemon

typewhisper record start
typewhisper record stop
typewhisper record toggle
typewhisper record cancel
typewhisper status
```

> **Recommendation:** Make start and stop idempotent. If start arrives while already recording, return OK and do nothing. If stop arrives while idle, return OK or a benign no-op. This prevents stuck states when compositors repeat keys or users release a key after a crash.

## State machine

| Current state | Event | Next state | Notes |
| --- | --- | --- | --- |
| idle | record start | recording | Open mic stream, show overlay/tray state, optionally beep. |
| recording | record stop | transcribing | Finalize audio buffer, release mic if appropriate, run ASR. |
| recording | record cancel | idle | Drop audio buffer and reset UI. |
| transcribing | record cancel | idle | Cancel ASR if safe, otherwise suppress output. |
| transcribing | ASR complete | injecting | Post-process text and send to output backend. |
| injecting | insertion complete | idle | Restore clipboard if needed, reset UI. |

## Backend priority

| Priority | Trigger backend | Where it fits | Reason |
| --- | --- | --- | --- |
| 1 | Compositor keybindings to CLI | Hyprland, Sway, River, Niri, other wlroots-style desktops | Best first release path. Reliable and transparent. Supports key release on many compositors. |
| 2 | XDG Global Shortcuts portal | GNOME/KDE and desktops with portal implementation | Secure user-approved path. Supports Activated and Deactivated signals in the spec. Implementation quality varies by desktop. |
| 3 | Desktop custom shortcut to CLI or D-Bus | GNOME/KDE fallback | Very reliable for toggle, often not true PTT because many desktop shortcut systems expose only activation. |
| 4 | Opt-in evdev listener | Any Linux desktop if the user grants permissions | Best universal true PTT fallback, but sensitive permissions and careful security messaging are required. |
| 5 | Focused window listener | Only Typewhisper foreground window | Keep as a convenience, not as the Linux Wayland solution. |

# .NET 10 implementation plan

.NET 10 is a good fit for this design. Use the .NET Generic Host for lifetime, dependency injection, logging, configuration, and hosted background services. Microsoft documents .NET 10 as a long-term support release, and the Generic Host is the standard lifetime-management pattern for console and worker-style apps. [S1] [S2]

## Project layout

Suggested solution layout:
```text
Typewhisper.sln
  src/Typewhisper.App/                 # UI/tray app, may also host daemon
  src/Typewhisper.Cli/                 # Thin CLI client, optional same binary
  src/Typewhisper.Core/                # State machine, recording, ASR pipeline
  src/Typewhisper.Linux/               # Linux-specific backends
      IHotkeyBackend.cs
      PortalGlobalShortcutBackend.cs
      EvdevHotkeyBackend.cs
      CompositorConfigWriter.cs
      PortalTextInjector.cs
      WTypeTextInjector.cs
      YdotoolTextInjector.cs
      ClipboardTextInjector.cs
      UnixSocketIpcServer.cs
      UnixSocketIpcClient.cs
      DBusTypewhisperService.cs
```

## Generic Host skeleton

Daemon process:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<DictationController>();
builder.Services.AddSingleton<IRecorder, Recorder>();
builder.Services.AddSingleton<ITranscriber, WhisperTranscriber>();
builder.Services.AddSingleton<ITextInjector, LinuxTextInjectorChain>();
builder.Services.AddHostedService<UnixSocketIpcServer>();
builder.Services.AddHostedService<PortalShortcutService>();      // optional
builder.Services.AddHostedService<EvdevHotkeyService>();         // opt-in only
builder.Services.AddHostedService<TrayOrUiService>();            // if applicable

IHost host = builder.Build();
await host.RunAsync();
```

## Command and IPC model

Use a Unix domain socket under XDG_RUNTIME_DIR. This is fast, local to the user session, and available in .NET through System.Net.Sockets. Prefer a socket path such as $XDG_RUNTIME_DIR/typewhisper/control.sock. If XDG_RUNTIME_DIR is missing, fall back to /tmp/typewhisper-$UID/control.sock with secure permissions. [S6]

JSON line protocol:
```csharp
public sealed record IpcRequest(string Command, string? Trigger = null);
public sealed record IpcResponse(string Status, string? State, string? Message);

// Commands:
//   record.start
//   record.stop
//   record.toggle
//   record.cancel
//   status
```

State controller skeleton:
```csharp
public enum DictationState
{
    Idle,
    Recording,
    Transcribing,
    Injecting
}

public sealed class DictationController
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DictationState _state = DictationState.Idle;

    public async Task StartAsync(string trigger, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != DictationState.Idle)
                return;

            _state = DictationState.Recording;
            // Start microphone capture and UI feedback here.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(string trigger, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != DictationState.Recording)
                return;

            _state = DictationState.Transcribing;
            // Finalize buffer, transcribe, then inject text.
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

## D-Bus service design

D-Bus is useful because desktop shortcuts, shell extensions, scripts, and trigger helpers can call it without needing to know where your socket lives. In .NET, Tmds.DBus.Protocol is the most relevant library to evaluate because it is intended for modern .NET, supports low-allocation protocol usage, and documents NativeAOT/trimming compatibility paths for .NET 8 or later. [S7]

Suggested D-Bus API:
```text
Bus name:     com.typewhisper.Typewhisper
Object path:  /com/typewhisper/Typewhisper
Interface:    com.typewhisper.Typewhisper1

Methods:
  StartRecording(trigger: s) -> ()
  StopRecording(trigger: s) -> ()
  ToggleRecording(trigger: s) -> ()
  CancelRecording(trigger: s) -> ()
  GetStatus() -> (state: s)

Signals:
  StateChanged(state: s)
```

# Hotkey implementation details

## Compositor-owned keybindings

This is the fastest way to fix the issue you described. The compositor sees Ctrl+Shift+Space no matter which application is focused. It then executes your CLI command. For true push-to-talk, the compositor must support a release binding.

Hyprland:
```ini
# ~/.config/hypr/hyprland.conf
# Start recording when Ctrl+Shift+Space is pressed.
bind = CTRL SHIFT, SPACE, exec, typewhisper record start

# Stop recording when Ctrl+Shift+Space is released.
bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop

# Optional emergency cancel.
bind = CTRL SHIFT, ESCAPE, exec, typewhisper record cancel
```

Sway:
```ini
# ~/.config/sway/config
bindsym --no-repeat Ctrl+Shift+space exec typewhisper record start
bindsym --release Ctrl+Shift+space exec typewhisper record stop
bindsym Ctrl+Shift+Escape exec typewhisper record cancel
```

Niri concept:
```kdl
// ~/.config/niri/config.kdl
binds {
    Ctrl+Shift+Space { spawn "typewhisper" "record" "start"; }
    Ctrl+Shift+Escape { spawn "typewhisper" "record" "cancel"; }
}

// Check the current Niri release documentation for release-binding syntax.
// If release bindings are unavailable, use toggle mode or evdev fallback.
```

> **Recommendation:** Your setup wizard should detect XDG_CURRENT_DESKTOP and WAYLAND_DISPLAY, then show the exact compositor snippet the user needs. For wlroots compositors, this is more reliable than trying to register a shortcut inside the app.

## XDG Global Shortcuts portal

The Global Shortcuts portal lets applications create a shortcut session, bind shortcuts, and receive global shortcut signals regardless of the focused window. The current portal interface documents both Activated and Deactivated signals. That makes true push-to-talk possible if the desktop backend emits Deactivated reliably. [S4]

- Create a session with org.freedesktop.portal.GlobalShortcuts.CreateSession.
- Bind one shortcut ID, for example "push-to-talk", with description "Start or stop Typewhisper dictation" and a preferred Ctrl+Shift+Space trigger.
- On Activated, call DictationController.StartAsync.
- On Deactivated, call DictationController.StopAsync.
- If the backend only behaves like a toggle or does not support the portal well, fall back to desktop custom shortcuts or evdev.

> **Caution:** Do not assume the portal works the same on every desktop. Treat it as a capability. Probe for the portal, ask the user to bind the shortcut, then run an interactive test that confirms both activation and deactivation before enabling PTT mode.

## GNOME custom shortcut fallback

GNOME custom shortcuts are reliable for toggle actions because GNOME owns the shortcut. OpenWhispr uses this kind of approach on GNOME Wayland and documents that GNOME Wayland does not provide push-to-talk through that custom shortcut path. [A1] [S8]

GNOME toggle fallback:
```bash
# Do not overwrite the user's existing custom-keybindings list.
# Read it first, append your path, then write the merged list.

PATH_ID="/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/typewhisper/"

# Example only. A real installer should merge with existing entries.
gsettings set org.gnome.settings-daemon.plugins.media-keys \
  custom-keybindings "['$PATH_ID']"

gsettings set \
  org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:$PATH_ID \
  name 'Typewhisper Dictation'

gsettings set \
  org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:$PATH_ID \
  command 'typewhisper record toggle'

gsettings set \
  org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:$PATH_ID \
  binding '<Control><Shift>space'
```

## KDE custom shortcut fallback

For KDE, the safest user-facing path is to guide the user through System Settings > Keyboard > Shortcuts > Custom Shortcuts and bind Ctrl+Shift+Space to typewhisper record toggle. Automation through KGlobalAccel/D-Bus can be added later, but a setup wizard should not depend on that automation for first release.

## Opt-in evdev fallback

The evdev fallback reads Linux input events from /dev/input/event*. This works outside the focused app because it reads from kernel input devices, not from the Wayland compositor. It is powerful and sensitive. The user must grant permission, usually by joining the input group or through a udev rule. Voxtype, stt2desktop, hyprwhspr, WhisperTux, and linux-whisper use this family of approach. [A5] [A8] [A9] [A12] [A15]

- Default it off. Enable only after the user explicitly chooses "Advanced: system input hotkey".
- Track only the configured combo. Do not log all keypresses. Do not expose a live key history.
- Support right and left modifier variants: left/right Ctrl, left/right Shift, and Space.
- Aggregate state across all keyboard devices because modifiers and Space may arrive from different devices.
- Use udev/inotify or periodic rescans to handle keyboard hotplug.
- Stop recording on any required key release so the app cannot get stuck recording.

Evdev combo tracking concept:
```csharp
// Linux input constants for Ctrl+Shift+Space.
private const ushort EV_KEY = 0x01;
private const ushort KEY_LEFTCTRL = 29;
private const ushort KEY_RIGHTCTRL = 97;
private const ushort KEY_LEFTSHIFT = 42;
private const ushort KEY_RIGHTSHIFT = 54;
private const ushort KEY_SPACE = 57;

private static bool IsComboDown(HashSet<ushort> keys)
{
    bool ctrl = keys.Contains(KEY_LEFTCTRL) || keys.Contains(KEY_RIGHTCTRL);
    bool shift = keys.Contains(KEY_LEFTSHIFT) || keys.Contains(KEY_RIGHTSHIFT);
    bool space = keys.Contains(KEY_SPACE);
    return ctrl && shift && space;
}
```

> **Recommendation:** If you implement evdev in C#, consider libevdev through P/Invoke for production portability. Directly parsing input_event is simple on common 64-bit Linux systems, but libevdev handles edge cases better.

# Text insertion backend chain

Hotkey capture and text insertion are different problems. Your hotkey can be perfect and insertion can still fail if the focused app or compositor rejects synthetic typing. The best projects use a fallback chain instead of one mechanism.

| Priority | Insertion backend | Best fit | Notes |
| --- | --- | --- | --- |
| 1 | XDG Remote Desktop portal or libei/eitype | GNOME/KDE, sandboxed environments, permissioned desktops | User-approved and Wayland-native. The portal documents keyboard keycode and keysym press/release notifications after keyboard access is granted. |
| 2 | wtype | wlroots compositors such as Sway and Hyprland | Good direct text typing on many Wayland setups. Does not work everywhere. |
| 3 | dotool or ydotool | Fallback for X11/Wayland if uinput is configured | Requires daemon or uinput permissions. Useful but more invasive. |
| 4 | Clipboard plus paste accelerator | Universal fallback | Overwrites clipboard unless you save/restore. Some apps intercept paste differently. |
| 5 | IBus input method architecture | GNOME/KDE/GTK/Qt input-aware apps | Most correct insertion model, but requires turning Typewhisper into an input method or companion engine. |

Speed of Sound and voic.sh both point toward portal-first insertion. Voxtype, linux-whisper, OpenWhispr, waystt, and WhisperTux demonstrate practical fallbacks with wtype, ydotool, wl-clipboard, or ydotoold. [A6] [A7] [A5] [A15] [A19] [A11] [A12]

Recommended insertion chain:
```text
ITextInjectorChain order for Linux Wayland:

1. PortalTextInjector        # if permission/session available
2. WTypeTextInjector         # if wtype exists and compositor supports it
3. DotoolTextInjector        # if dotool exists and is configured
4. YdotoolTextInjector       # if ydotoold/uinput is configured
5. ClipboardPasteInjector    # save clipboard, wl-copy text, paste, restore
```

# Packaging and setup for Linux

## Publishing .NET 10 binaries

For the daemon/UI, self-contained publishing is safer than NativeAOT until all audio, D-Bus, portal, and UI dependencies are proven AOT-safe. NativeAOT can be attractive for the CLI because it gives fast startup and no runtime dependency, but it has trimming and dynamic-loading limits. Microsoft documents NativeAOT as ahead-of-time compiled, self-contained, faster to start, and with smaller memory footprint, but also lists limitations around dynamic loading and trimming. [S3]

Publish examples:
```bash
# Safer first release: self-contained Linux x64 build.
dotnet publish src/Typewhisper.App \
  -c Release -r linux-x64 --self-contained true

# Optional fast CLI if your dependencies are AOT-safe.
# In the CLI csproj:
# <PublishAot>true</PublishAot>
dotnet publish src/Typewhisper.Cli \
  -c Release -r linux-x64
```

## Systemd user service

User service:
```ini
# ~/.config/systemd/user/typewhisper.service
[Unit]
Description=Typewhisper dictation daemon

[Service]
ExecStart=%h/.local/bin/typewhisper daemon
Restart=on-failure
RestartSec=2

[Install]
WantedBy=default.target
```

Enable service:
```bash
systemctl --user daemon-reload
systemctl --user enable --now typewhisper.service
systemctl --user status typewhisper.service
```

## Desktop file

Desktop entry:
```ini
# ~/.local/share/applications/com.typewhisper.Typewhisper.desktop
[Desktop Entry]
Type=Application
Name=Typewhisper
Exec=typewhisper
Icon=com.typewhisper.Typewhisper
Categories=Utility;AudioVideo;Accessibility;
StartupNotify=true

# Add this only if you implement D-Bus activation properly.
# DBusActivatable=true
```

## Permissions and user messaging

| Permission | Why Typewhisper needs it | Recommended UX |
| --- | --- | --- |
| Microphone | Required for recording. | Ask through the desktop/UI and show a device test. |
| Portal shortcut | Required for portal-managed global shortcuts. | User sees a system dialog. Provide a retry and reset path. |
| Portal keyboard injection | Required for Remote Desktop portal typing. | Explain that it is used only to type the transcript. |
| input group or udev rule | Required for evdev hotkey fallback. | Warn that input permissions are sensitive. Default off. |
| /dev/uinput or ydotoold | Required by ydotool/dotool injection fallback. | Offer only when wtype/portal is unavailable. |
| Clipboard access | Required for clipboard fallback. | Save and restore clipboard where possible. Tell user it may briefly replace clipboard contents. |

# Setup wizard behavior

1. Detect session: WAYLAND_DISPLAY, XDG_SESSION_TYPE, XDG_CURRENT_DESKTOP, DESKTOP_SESSION, and compositor-specific environment variables.
1. Show the recommended backend for the detected environment: compositor keybinding, portal, desktop custom shortcut, or evdev fallback.
1. Create or show the exact binding for Ctrl+Shift+Space. Do not silently overwrite existing shortcuts.
1. Run an interactive test: press Ctrl+Shift+Space, confirm Typewhisper receives start; release it, confirm Typewhisper receives stop.
1. If release is not observed, offer toggle mode or evdev fallback.
1. Test text insertion into a small focused test field, then into a real external app.
1. Write diagnostics to a support bundle without logging spoken text or unrelated keystrokes.

Decision logic:
```text
Detected: GNOME Wayland
Recommended: XDG Global Shortcuts portal if available.
Fallback: GNOME custom shortcut to "typewhisper record toggle".
True PTT fallback: advanced evdev hotkey with input permissions.

Detected: Hyprland
Recommended: add bind and bindr lines to hyprland.conf.
Fallback: evdev hotkey.

Detected: Sway
Recommended: bindsym plus bindsym --release.
Fallback: evdev hotkey.

Detected: KDE Plasma Wayland
Recommended: portal or KDE custom shortcut toggle.
True PTT fallback: portal if Deactivated works, otherwise evdev.
```

# User-facing Linux Wayland documentation draft

The following copy can be adapted into Typewhisper documentation.

Documentation draft:
```markdown
# Typewhisper on Wayland

Wayland does not let normal applications read global keyboard shortcuts from
other focused apps. Because of that, Typewhisper uses your desktop or compositor
to trigger dictation.

## Hyprland

Add this to ~/.config/hypr/hyprland.conf:

bind = CTRL SHIFT, SPACE, exec, typewhisper record start
bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop

Reload Hyprland. Hold Ctrl+Shift+Space, speak, then release.

## Sway

Add this to ~/.config/sway/config:

bindsym --no-repeat Ctrl+Shift+space exec typewhisper record start
bindsym --release Ctrl+Shift+space exec typewhisper record stop

Reload Sway. Hold Ctrl+Shift+Space, speak, then release.

## GNOME or KDE

Use Typewhisper Settings > Shortcuts > Set up global shortcut.
If the portal setup is unavailable, create a custom keyboard shortcut:

Command:  typewhisper record toggle
Shortcut: Ctrl+Shift+Space

This mode toggles recording instead of holding while pressed.
For true push-to-talk on desktops that do not expose release events,
use the advanced evdev hotkey option in Typewhisper Settings.
```

# Testing matrix

| Environment | Test focus | Apps to test |
| --- | --- | --- |
| GNOME Wayland | Portal trigger, custom shortcut toggle, evdev fallback, portal insertion. | LibreOffice Writer, Firefox, GNOME Text Editor, Terminal. |
| KDE Plasma Wayland | Portal trigger, custom shortcut toggle, evdev fallback, portal/wtype insertion. | Kate, Konsole, Firefox, LibreOffice. |
| Hyprland | bind/bindr true PTT, wtype, ydotool fallback. | Kitty/Alacritty, VS Code, browser, text editor. |
| Sway | bindsym and --release true PTT, wtype, clipboard fallback. | Terminal, Firefox, text editor. |
| X11 session | Existing global shortcut path, xdotool insertion. | Regression coverage. |

- Press and release Ctrl+Shift+Space 50 times with no speech. Ensure no stuck recording state.
- Hold the hotkey while switching focus between windows. Ensure stop still arrives on release.
- Crash and restart the daemon while the key is held. Ensure the next release or next press recovers.
- Test with multiple keyboards connected.
- Test with keyboard layouts other than US English.
- Test terminal paste behavior separately from GUI text fields.
- Test clipboard restore on large clipboard contents and images.
- Test concurrent commands: UI start, CLI stop, portal activation, and evdev release.

# Recommended implementation roadmap

| Phase | Scope | Why |
| --- | --- | --- |
| Phase 1 | Daemon, state machine, CLI, Unix socket IPC, compositor docs for Hyprland/Sway. | Fixes the exact foreground-window failure quickly. |
| Phase 2 | Text insertion chain: wtype, ydotool/dotool, clipboard fallback. | Makes transcription output reliable across common Wayland setups. |
| Phase 3 | D-Bus service and GNOME/KDE custom shortcut setup helper. | Improves desktop integration and toggle-mode reliability. |
| Phase 4 | XDG Global Shortcuts portal with Activated/Deactivated test. | Adds secure user-approved PTT where supported. |
| Phase 5 | Remote Desktop portal/libei/eitype insertion backend. | Best Wayland-native typing path where available. |
| Phase 6 | Opt-in evdev hotkey backend and udev/input group setup. | Universal true PTT fallback for users who accept the permission tradeoff. |

# Final recommendation

**Build Typewhisper like Voxtype and Whisper-Wayland, then add Speed of Sound style portal support.** That means daemon plus CLI first, compositor shortcuts for true push-to-talk on wlroots-style desktops, XDG Global Shortcuts portal where it works, GNOME/KDE custom shortcut fallback for toggle mode, and opt-in evdev for users who need true push-to-talk everywhere. For text insertion, use a chain rather than a single method: portal/libei, wtype, dotool/ydotool, then clipboard.

This design gives you the best balance of Wayland correctness, security, user trust, and broad Linux desktop coverage. It also maps cleanly to .NET 10 because the Generic Host gives you a stable daemon lifecycle, Unix domain sockets give you a simple local control plane, and D-Bus/portal bindings can be layered on without changing the recording state machine.

# Sources and documentation reviewed

**[S1]** [.NET 10 overview, Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)

**[S2]** [.NET Generic Host, Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host)

**[S3]** [Native AOT deployment, Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

**[S4]** [XDG Desktop Portal Global Shortcuts](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html)

**[S5]** [XDG Desktop Portal Remote Desktop](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html)

**[S6]** [System.Net.Sockets namespace, UnixDomainSocketEndPoint, Microsoft Learn](https://learn.microsoft.com/en-us/dotNet/API/system.net.sockets)

**[S7]** [Tmds.DBus for .NET](https://github.com/tmds/Tmds.DBus)

**[S8]** [GSettings command line tool, GNOME/GIO reference](https://gnome.pages.gitlab.gnome.org/libsoup/gio/gsettings-tool.html)

**[A1]** [OpenWhispr global hotkeys documentation](https://www.mintlify.com/OpenWhispr/openwhispr/configuration/hotkeys)

**[A2]** [OpenWhispr repository](https://github.com/OpenWhispr/openwhispr)

**[A3]** [Whispering / Epicenter commands.ts](https://github.com/EpicenterHQ/epicenter/blob/4e539857cf3cda9868c0cec1c3b9a5e6e1b4d503/apps/whispering/src/lib/commands.ts)

**[A4]** [Whispering / Epicenter global shortcut manager](https://github.com/EpicenterHQ/epicenter/blob/4e539857cf3cda9868c0cec1c3b9a5e6e1b4d503/apps/whispering/src/lib/services/desktop/global-shortcut-manager.ts)

**[A5]** [Voxtype repository and README](https://github.com/peteonrails/voxtype)

**[A6]** [Voxtype compositor integration docs](https://peteonrails-voxtype.mintlify.app/guides/compositor-integration)

**[A7]** [Speed of Sound keyboard shortcut documentation](https://github.com/zugaldia/speedofsound/blob/d2563af098020a6a350428bec153e8c26d2c9575/docs/keyboard-shortcut.md)

**[A8]** [Speed of Sound FAQ](https://www.speedofsound.io/faq/)

**[A9]** [hyprwhspr project site](https://hyprwhspr.com/)

**[A10]** [stt2desktop PyPI project description](https://pypi.org/project/stt2desktop/)

**[A11]** [waystt repository and README](https://github.com/sevos/waystt)

**[A12]** [WhisperTux repository and README](https://github.com/cjams/whispertux)

**[A13]** [BlahST repository and README](https://github.com/QuantiusBenignus/BlahST)

**[A14]** [Handy repository and README](https://github.com/cjpais/handy)

**[A15]** [linux-whisper project site](https://kwap.github.io/linux-whisper/)

**[A16]** [whispers crate documentation](https://docs.rs/crate/whispers/latest)

**[A17]** [Hyprvoice repository and README](https://github.com/LeonardoTrapani/hyprvoice)

**[A18]** [IBus Speech To Text repository and README](https://github.com/PhilippeRo/IBus-Speech-To-Text)

**[A19]** [voic.sh Wayland voice typing](https://voic.sh/)

**[A20]** [Dikt GNOME/Wayland speech-to-text](https://dikt.tequerist.com/)

**[A21]** [Whisper-Wayland __main__.py](https://github.com/Andrewske/whisper-wayland/blob/Main/src/__main__.py)

**[A22]** [Whisper-Wayland commands.py](https://github.com/Andrewske/whisper-wayland/blob/Main/src/commands.py)

> **Caution:** Some names in the original list are generic or overlap with renamed projects. Where a distinct repository was not clearly identifiable, this document uses the closest current project matching the Wayland speech-to-text behavior: Dikt for Dictate-style GNOME/IBus dictation and voic.sh for Wayland Voice Typer style injection.
