# Phase 6 — DE-Specific Helpers

Status: blocked on Phase 5
Depends on: Phase 5 (full CLI surface to bind to)
Unblocks: nothing — this is polish

## Goal

Reduce the manual setup burden for users on each major desktop environment by writing or proposing the right shortcut binding automatically. Phase 4 + 5 made it *possible* to bind shortcuts; Phase 6 makes it *easy*.

Each helper is independent. Ship them in any order.

## Scope

In scope:

- **GNOME helper**: write a custom keybinding via `gsettings` (merge with existing list, never overwrite).
- **KDE helper**: register a global shortcut via KGlobalAccel D-Bus, or write a `.desktop` file with `X-KDE-Shortcuts`.
- **Hyprland helper**: append `bind` + `bindr` lines to `hyprland.conf`, or use `hyprctl keyword bind` at runtime.
- **Sway helper**: same idea — append to `~/.config/sway/config` or send to running Sway via IPC.
- Backend switcher / status panel surfaces a "Set up automatically" button per detected desktop.

Out of scope:

- Niri, river, other compositor support — defer until users ask.
- Auto-removal of TypeWhisper's bindings on uninstall (low value; document a manual cleanup command).
- Conflict detection (warn if `Ctrl+Shift+Space` is already bound somewhere). Add later if users report collisions.

## Files

### New

```
src/TypeWhisper.Linux/Services/Hotkey/DeSetup/IDeShortcutWriter.cs
src/TypeWhisper.Linux/Services/Hotkey/DeSetup/GnomeShortcutWriter.cs
src/TypeWhisper.Linux/Services/Hotkey/DeSetup/KdeShortcutWriter.cs
src/TypeWhisper.Linux/Services/Hotkey/DeSetup/HyprlandShortcutWriter.cs
src/TypeWhisper.Linux/Services/Hotkey/DeSetup/SwayShortcutWriter.cs
src/TypeWhisper.Linux/Services/Hotkey/DeSetup/DesktopDetector.cs
```

### Modified

```
src/TypeWhisper.Linux/Views/Settings/ShortcutsSettingsView.axaml   # "Set up automatically" button per DE
src/TypeWhisper.Linux/Services/Hotkey/BackendSelector.cs           # reflect DE-written shortcuts in status
```

## Interface

```csharp
public interface IDeShortcutWriter
{
    string DesktopId { get; }                                 // "gnome", "kde", "hyprland", "sway"
    bool IsCurrentDesktop();
    bool SupportsPushToTalk { get; }                          // true for hyprland/sway, false for gnome/kde
    Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct);
    Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct);
}

public sealed record DeShortcutSpec(
    string ShortcutId,                  // "typewhisper.dictation.toggle"
    string DisplayName,                 // shown in DE settings
    string Trigger,                     // "Ctrl+Shift+Space"
    string OnPressCommand,              // "typewhisper" (toggle) or "typewhisper record start"
    string? OnReleaseCommand);          // null for toggle-only DEs

public sealed record DeShortcutWriteResult(
    bool Success,
    string? UserMessage,
    string[] FilesChanged);
```

## GNOME helper

GNOME custom keybindings live in `org.gnome.settings-daemon.plugins.media-keys`. The list of paths is in `custom-keybindings`; each path holds `name`, `command`, `binding`.

Critical: **never overwrite the list**. Read the current value, append our path if missing, write the merged list back.

```csharp
public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
{
    var path = $"/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/typewhisper-{spec.ShortcutId.GetHashCode():X8}/";

    // 1. Read existing list.
    var current = await RunAsync("gsettings", "get",
        "org.gnome.settings-daemon.plugins.media-keys", "custom-keybindings", ct);
    var list = ParseGSettingsList(current.Stdout);  // returns List<string>
    if (!list.Contains(path)) list.Add(path);

    // 2. Write merged list.
    await RunAsync("gsettings", "set",
        "org.gnome.settings-daemon.plugins.media-keys",
        "custom-keybindings", FormatGSettingsList(list), ct);

    // 3. Set our entry's fields.
    var schemaWithPath = $"org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:{path}";
    await RunAsync("gsettings", "set", schemaWithPath, "name", spec.DisplayName, ct);
    await RunAsync("gsettings", "set", schemaWithPath, "command", spec.OnPressCommand, ct);
    await RunAsync("gsettings", "set", schemaWithPath, "binding", FormatGnomeAccel(spec.Trigger), ct);

    return new DeShortcutWriteResult(true, "GNOME shortcut set up.", new[] { "gsettings: " + path });
}
```

`FormatGnomeAccel`: converts `Ctrl+Shift+Space` to `<Control><Shift>space`.

GNOME has no release event, so `OnReleaseCommand` is ignored and `SupportsPushToTalk = false`.

## KDE helper

Two approaches; pick **the `.desktop` file approach** for simplicity:

- Drop a `.desktop` file into `~/.local/share/kglobalaccel/` with `X-KDE-Shortcuts=Ctrl+Shift+Space`.
- KGlobalAccel picks it up; user can edit in System Settings → Shortcuts.

The full D-Bus path via `org.kde.kglobalaccel` is messier and offers no benefit for a single static shortcut.

```ini
# ~/.local/share/kglobalaccel/com.typewhisper.dictation.desktop
[Desktop Entry]
Type=Service
Name=TypeWhisper: Toggle Dictation
Exec=typewhisper
X-KDE-Shortcuts=Ctrl+Shift+Space
```

Toggle-only (KDE shortcuts don't expose release).

## Hyprland helper

Two modes:

1. **Runtime bind** via `hyprctl keyword bind` — instant, but doesn't survive Hyprland restart.
2. **Config write** — append to `~/.config/hypr/hyprland.conf` between sentinel comments so we can update/remove cleanly.

Do both: runtime bind for immediate effect, config write for persistence. Wrap our additions in sentinel comments:

```ini
# >>> typewhisper:dictation (managed; do not edit between sentinels)
bind  = CTRL SHIFT, SPACE, exec, typewhisper record start
bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop
bind  = CTRL SHIFT, ESCAPE, exec, typewhisper record cancel
# <<< typewhisper:dictation
```

Removal: parse the file, drop everything between sentinels.

`SupportsPushToTalk = true` (Hyprland is the reason this phase even tries to write release binds).

## Sway helper

Same idea as Hyprland but for `~/.config/sway/config`:

```ini
# >>> typewhisper:dictation (managed; do not edit between sentinels)
bindsym --no-repeat Ctrl+Shift+space exec typewhisper record start
bindsym --release Ctrl+Shift+space exec typewhisper record stop
bindsym Ctrl+Shift+Escape exec typewhisper record cancel
# <<< typewhisper:dictation
```

Reload Sway after writing: `swaymsg reload`.

`SupportsPushToTalk = true`.

## Desktop detection

```csharp
public static class DesktopDetector
{
    public static string DetectId()
    {
        var current = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.ToLowerInvariant() ?? "";
        if (current.Contains("hyprland")) return "hyprland";
        if (current.Contains("sway"))     return "sway";
        if (current.Contains("gnome"))    return "gnome";
        if (current.Contains("kde"))      return "kde";
        // Fall back to session-specific hints
        if (Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is not null) return "hyprland";
        if (Environment.GetEnvironmentVariable("SWAYSOCK") is not null) return "sway";
        return "unknown";
    }
}
```

## Settings panel addition

The Phase 4 "Bind a custom shortcut" section grows a "Set up automatically" button when a known DE is detected:

```
┌─ Bind a custom shortcut ────────────────────────────────┐
│                                                          │
│  Detected: Hyprland                                      │
│                                                          │
│  [ Set up automatically ]  [ Show me the commands ]      │
│                                                          │
│  This will add the following lines to your hyprland.conf │
│  inside a managed block:                                 │
│                                                          │
│      bind  = CTRL SHIFT, SPACE, exec, typewhisper …      │
│      bindr = CTRL SHIFT, SPACE, exec, typewhisper …      │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

Clicking "Set up automatically" calls the appropriate `IDeShortcutWriter.WriteAsync`, shows the result, and reminds the user to reload (Sway only — Hyprland and runtime-bound configs apply immediately).

## Exit criteria

- On GNOME Wayland, "Set up automatically" creates the custom keybinding and `Ctrl+Shift+Space` toggles dictation from any focused app.
- On KDE Plasma Wayland: same, via the `.desktop` file approach.
- On Hyprland: press+release binds work; PTT/Hybrid modes functional without `input` group.
- On Sway: same as Hyprland.
- "Set up automatically" never overwrites existing user keybindings — it only appends or merges.
- Removing TypeWhisper (or clicking "Remove integration" in settings) cleanly removes the managed block / list entry.

## What this phase explicitly does NOT do

- Does NOT add support for every Wayland compositor in existence. Hyprland, Sway, GNOME, KDE cover ~95% of the target users.
- Does NOT detect or warn about pre-existing conflicting shortcuts. The user will hear about it from us (they'll try it and see something didn't toggle).
- Does NOT touch system-wide config files. Everything is per-user.
- Does NOT require sudo. Every writer operates in the user's home or session bus.

## Risks

| Risk | Mitigation |
|---|---|
| GNOME `custom-keybindings` list parse mistake → wipes user's other shortcuts. | Strict gsettings list parser with round-trip tests. Backup before write: capture current list, write to `~/.config/typewhisper/backups/gnome-keybindings-{timestamp}.txt`. Show the user how to restore. |
| Sentinel-comment block edited by the user, then we try to update. | Detect mismatched sentinels (open without close, etc.) and refuse to write; surface "Your config has an open managed block. Remove it manually and retry." |
| Hyprland `hyprctl` binary not in PATH. | Detect at `IsCurrentDesktop()` — if `hyprctl` is missing, return false even if the env var says Hyprland. Show "Hyprland detected but `hyprctl` not available." |
| KDE `.desktop` file location varies across distros. | Use the XDG path `~/.local/share/kglobalaccel/`; fall back to `~/.local/share/applications/` with `X-KDE-Shortcut`. |
| User runs the writer twice — duplicate entries. | Idempotent writers: each implementation checks for an existing managed block / entry first. |
| Removal leaves the entry on disk but TypeWhisper isn't installed anymore. | Document a one-shot cleanup command: `typewhisper cleanup-shortcuts` (or per-DE manual steps in the README). Not worth automating beyond that. |
