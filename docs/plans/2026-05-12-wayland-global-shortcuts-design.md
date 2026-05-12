# Wayland Global Shortcuts Design

Date: 2026-05-12

## Goal

TypeWhisper needs a Linux global shortcut implementation that works on modern
Wayland desktops without depending on X11 keyboard hooks. X11 support should
remain intact, but Wayland must be treated as the primary Linux path.

The current X11 implementation uses SharpHook/libuiohook. That is expected to
work on X11, but it is not a correct Wayland strategy because Wayland
compositors intentionally block normal applications from globally observing
keyboard events.

## Target Behavior

- X11 sessions keep using the existing SharpHook backend.
- Wayland sessions use XDG Desktop Portal GlobalShortcuts as the primary
  backend.
- If the portal backend is unavailable or unsupported, compositor-specific
  fallback backends may be used.
- Manual command binding remains a final troubleshooting fallback.
- Wayland command/portal shortcuts should use toggle mode only unless a backend
  can provide reliable press and release events.
- The Shortcuts page must clearly show which backend is active and whether
  push-to-talk/hybrid modes are available.

## Backend Priority

Use this priority order:

1. X11: SharpHook/libuiohook.
2. Wayland: XDG Desktop Portal `org.freedesktop.portal.GlobalShortcuts`.
3. Fallback: GNOME custom shortcut registration.
4. Fallback: KDE KGlobalAccel registration.
5. Fallback: Hyprland runtime binding.
6. Fallback: show command for manual compositor binding.

The portal path should be attempted before any GNOME/KDE/Hyprland-specific
logic. Compositor-specific code is only a fallback, not the main Wayland model.

## Current Findings

During Fedora GNOME Wayland testing:

- GNOME custom shortcuts were writable through `gsettings`.
- The custom shortcut entry was created and listed in:
  `org.gnome.settings-daemon.plugins.media-keys custom-keybindings`.
- `Ctrl+Shift+Space` did fire when the command was temporarily changed to:
  `bash -lc 'date >> /tmp/typewhisper-shortcut-test.log'`.
- The previous HTTP/CLI callback path was brittle because it depended on local
  API state, bearer token state, and command environment.

This proves GNOME shortcut registration can work as a fallback, but it should
not be the main Wayland implementation.

## Proposed Interfaces

Create a backend abstraction so hotkey registration is not embedded directly in
`HotkeyService`.

```csharp
public interface IGlobalShortcutBackend : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsPressRelease { get; }
    bool IsAvailable();
    Task<GlobalShortcutRegistrationResult> RegisterAsync(
        GlobalShortcutSet shortcuts,
        CancellationToken ct);
    Task UnregisterAsync(CancellationToken ct);
}

public sealed record GlobalShortcutSet(
    string DictationHotkey,
    string? PromptPaletteHotkey,
    string? TransformSelectionHotkey,
    string? RecentTranscriptionsHotkey,
    string? CopyLastTranscriptionHotkey);

public sealed record GlobalShortcutRegistrationResult(
    bool Success,
    string BackendId,
    string UserMessage,
    bool RequiresToggleMode,
    string? TroubleshootingCommand = null);
```

`HotkeyService` should choose a backend, expose status, and raise the same
events it raises today:

- `DictationToggleRequested`
- `DictationStartRequested`
- `DictationStopRequested`
- `PromptPaletteRequested`
- `TransformSelectionRequested`
- `RecentTranscriptionsRequested`
- `CopyLastTranscriptionRequested`
- `CancelRequested`

## Wayland Portal Backend

Backend name: `XdgPortalGlobalShortcutsBackend`

Use D-Bus through a normal NuGet dependency, not shelling out to `gdbus`.
Recommended package:

```xml
<PackageReference Include="Tmds.DBus.Protocol" Version="0.92.0" />
```

This is a project dependency, not a local machine setup step. It should restore
as part of normal `dotnet restore/build`.

### Portal Objects

Service:

```text
org.freedesktop.portal.Desktop
```

Object path:

```text
/org/freedesktop/portal/desktop
```

Interface:

```text
org.freedesktop.portal.GlobalShortcuts
```

Relevant methods/signals:

```text
CreateSession(a{sv} options) -> o request_handle
BindShortcuts(o session_handle, a(sa{sv}) shortcuts, s parent_window, a{sv} options) -> o request_handle
ListShortcuts(o session_handle, a{sv} options) -> o request_handle
Activated(o session_handle, s shortcut_id, t timestamp, a{sv} options)
Deactivated(o session_handle, s shortcut_id, t timestamp, a{sv} options)
ShortcutsChanged(o session_handle, a(sa{sv}) shortcuts)
```

Also listen for request responses on:

```text
org.freedesktop.portal.Request.Response
```

### Portal Shortcut IDs

Use stable IDs:

```text
dictation.toggle
prompt_palette.toggle
selection.transform
recent_transcriptions.toggle
copy_last_transcription
```

### Portal Pseudocode

```csharp
class XdgPortalGlobalShortcutsBackend : IGlobalShortcutBackend
{
    Connection _connection;
    ObjectPath _sessionHandle;
    IDisposable _activatedSub;
    IDisposable _deactivatedSub;

    async Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        _connection = Connection.Session;
        await _connection.ConnectAsync();

        if (!await PortalInterfaceExists())
            return NotAvailable("GlobalShortcuts portal is not exposed.");

        var sessionToken = CreateStableToken("typewhisper_shortcuts");
        var createRequest = await CallCreateSession(new Dictionary<string, VariantValue>
        {
            ["session_handle_token"] = VariantValue.String(sessionToken),
            ["handle_token"] = VariantValue.String(CreateRequestToken())
        });

        var createResponse = await WaitForPortalResponse(createRequest, ct);
        if (createResponse.ResponseCode != 0)
            return Failed("Shortcut session was denied or cancelled.");

        _sessionHandle = createResponse.Results["session_handle"].ReadObjectPath();

        await SubscribeActivated();
        await SubscribeDeactivated();

        var portalShortcuts = new[]
        {
            Shortcut("dictation.toggle", "Toggle TypeWhisper dictation"),
            Shortcut("prompt_palette.toggle", "Open TypeWhisper prompt palette"),
            Shortcut("selection.transform", "Transform selected text with TypeWhisper"),
            Shortcut("recent_transcriptions.toggle", "Open TypeWhisper recent transcriptions"),
            Shortcut("copy_last_transcription", "Copy last TypeWhisper transcription")
        };

        var bindRequest = await CallBindShortcuts(
            _sessionHandle,
            portalShortcuts,
            parentWindow: "",
            options: new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(CreateRequestToken())
            });

        var bindResponse = await WaitForPortalResponse(bindRequest, ct);
        if (bindResponse.ResponseCode != 0)
            return Failed("Shortcut binding was denied or cancelled.");

        return Success(
            backendId: "xdg-portal-global-shortcuts",
            userMessage: "Wayland portal shortcuts are registered.",
            requiresToggleMode: false_or_true_based_on_press_release_support);
    }

    void OnActivated(ObjectPath session, string shortcutId, ulong timestamp)
    {
        if (session != _sessionHandle) return;

        switch (shortcutId)
        {
            case "dictation.toggle":
                DictationToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "prompt_palette.toggle":
                PromptPaletteRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "selection.transform":
                TransformSelectionRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "recent_transcriptions.toggle":
                RecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "copy_last_transcription":
                CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    void OnDeactivated(ObjectPath session, string shortcutId, ulong timestamp)
    {
        if (session != _sessionHandle) return;

        if (shortcutId == "dictation.toggle" && currentMode == PushToTalk)
            DictationStopRequested?.Invoke(this, EventArgs.Empty);
    }
}
```

### Portal Response Pseudocode

```csharp
async Task<PortalResponse> WaitForPortalResponse(ObjectPath requestPath, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<PortalResponse>();

    using var sub = await _connection.AddMatchAsync(
        MatchRule.Signal(
            sender: "org.freedesktop.portal.Desktop",
            path: requestPath,
            iface: "org.freedesktop.portal.Request",
            member: "Response"),
        reader: ReadPortalResponse,
        handler: (exception, response, _, _) =>
        {
            if (exception != null) tcs.TrySetException(exception);
            else tcs.TrySetResult(response);
        },
        ...);

    return await tcs.Task.WaitAsync(ct);
}
```

Response code meanings:

- `0`: success
- `1`: user cancelled
- `2`: other failure

## GNOME Fallback Backend

Backend name: `GnomeCustomShortcutBackend`

Only use when portal is unavailable.

This backend should not call the HTTP API. It should use a D-Bus method exposed
by TypeWhisper itself, so there is no bearer token, local API, or shell
environment problem.

### App-Owned D-Bus Callback Service

Service:

```text
com.typewhisper.App
```

Object path:

```text
/com/typewhisper/App
```

Interface:

```text
com.typewhisper.App
```

Methods:

```text
ToggleDictation()
OpenPromptPalette()
TransformSelection()
OpenRecentTranscriptions()
CopyLastTranscription()
Cancel()
```

GNOME command example:

```bash
gdbus call --session \
  --dest com.typewhisper.App \
  --object-path /com/typewhisper/App \
  --method com.typewhisper.App.ToggleDictation
```

### GNOME Registration Pseudocode

```csharp
class GnomeCustomShortcutBackend : IGlobalShortcutBackend
{
    const string Path = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/typewhisper/";

    async Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        if (!DesktopIsGnomeWayland()) return NotAvailable();
        if (!CommandExists("gsettings") || !CommandExists("gdbus")) return NotAvailable();

        EnsureTypeWhisperDbusServiceStarted();

        var paths = GSettingsGetStringArray(
            "org.gnome.settings-daemon.plugins.media-keys",
            "custom-keybindings");

        if (!paths.Contains(Path))
            paths.Add(Path);

        GSettingsSetStringArray(
            "org.gnome.settings-daemon.plugins.media-keys",
            "custom-keybindings",
            paths);

        var schema = $"org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:{Path}";

        GSettingsSet(schema, "name", "TypeWhisper Dictation");
        GSettingsSet(schema, "binding", ToGnomeAccelerator(shortcuts.DictationHotkey));
        GSettingsSet(schema, "command", BuildGdbusCommand("ToggleDictation"));

        return Success(
            backendId: "gnome-custom-shortcut",
            userMessage: "GNOME custom shortcut is registered.",
            requiresToggleMode: true);
    }
}
```

## KDE Fallback Backend

Backend name: `KdeKGlobalAccelBackend`

Only use when portal is unavailable.

KDE has its own global shortcut service. This needs a separate implementation
and should not be guessed from GNOME behavior. Before implementation, inspect
the current KDE D-Bus interfaces on a KDE Wayland machine:

```bash
qdbus org.kde.kglobalaccel
qdbus org.kde.kglobalaccel /kglobalaccel
busctl --user tree org.kde.kglobalaccel
busctl --user introspect org.kde.kglobalaccel /kglobalaccel
```

Expected shape:

```csharp
class KdeKGlobalAccelBackend : IGlobalShortcutBackend
{
    async Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        if (!DesktopIsKdeWayland()) return NotAvailable();

        // Register an action named "TypeWhisper Dictation".
        // Bind it to shortcuts.DictationHotkey.
        // Subscribe to KGlobalAccel action invocation signal.
        // Raise DictationToggleRequested when action fires.

        return Success("kde-kglobalaccel", "KDE global shortcut is registered.", requiresToggleMode: true);
    }
}
```

Do not mark KDE as implemented until tested on KDE Plasma Wayland.

## Hyprland Fallback Backend

Backend name: `HyprlandRuntimeBindBackend`

Only use when portal is unavailable.

Hyprland runtime binds do not persist across sessions, so TypeWhisper should
register them on each app start and each shortcut settings change.

```csharp
class HyprlandRuntimeBindBackend : IGlobalShortcutBackend
{
    async Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
    {
        if (!EnvironmentHas("HYPRLAND_INSTANCE_SIGNATURE")) return NotAvailable();
        if (!CommandExists("hyprctl")) return NotAvailable();

        var bind = ToHyprlandBind(shortcuts.DictationHotkey);
        Run("hyprctl", "keyword", "unbind", bind.KeySpec);
        Run("hyprctl", "keyword", "bind", $"{bind.Mods}, {bind.Key}, exec, {BuildGdbusCommand("ToggleDictation")}");

        return Success("hyprland-runtime-bind", "Hyprland shortcut is registered for this session.", requiresToggleMode: true);
    }
}
```

## Manual Fallback

When no automatic backend works, show a copyable command:

```bash
gdbus call --session \
  --dest com.typewhisper.App \
  --object-path /com/typewhisper/App \
  --method com.typewhisper.App.ToggleDictation
```

This keeps the fallback callback D-Bus based instead of HTTP based.

## UI Changes

Shortcuts page should show:

- Active backend name.
- Whether the shortcut is registered.
- Whether push-to-talk/hybrid are supported.
- Any action needed by the user.
- Fallback command only when automatic registration is unavailable.

Suggested UI text:

```text
Shortcut backend: XDG Desktop Portal
TypeWhisper registered the shortcut with your Wayland desktop.
```

Fallback example:

```text
Shortcut backend: Manual Wayland command
This compositor did not expose a supported shortcut registration API.
Bind this command in your desktop shortcut settings:
...
```

If the backend only supports command callbacks:

```text
This desktop only supports toggle-style shortcut callbacks. Push-to-talk and
hybrid are available on X11 or on Wayland backends that provide key release
events.
```

## Startup Flow

```csharp
settings.Load();

dbusCallbackService.Start(); // required for GNOME/KDE/Hyprland/manual fallback

hotkeyService.Initialize();
await hotkeyService.ApplyShortcutBackendAsync(settings.Current);

settings.SettingsChanged += async settings =>
{
    hotkeyService.UpdateBindings(settings);
    await hotkeyService.ApplyShortcutBackendAsync(settings);
};
```

Backend selection:

```csharp
if (SessionType == "x11")
    backend = SharpHookBackend;
else
    backend = FirstAvailable(
        XdgPortalGlobalShortcutsBackend,
        GnomeCustomShortcutBackend,
        KdeKGlobalAccelBackend,
        HyprlandRuntimeBindBackend,
        ManualCommandBackend);
```

## Testing Checklist

### Unit Tests

- Hotkey string conversion:
  - TypeWhisper format -> GNOME accelerator.
  - TypeWhisper format -> Hyprland bind syntax.
  - TypeWhisper format -> portal shortcut metadata.
- Backend selection by environment:
  - X11 selects SharpHook.
  - GNOME Wayland tries portal before GNOME fallback.
  - Hyprland Wayland tries portal before Hyprland fallback.
  - Unknown Wayland falls back to manual command.
- Portal response handling:
  - Success response.
  - User-cancelled response.
  - Portal unavailable.
  - Activation signal dispatch.
  - Deactivation signal dispatch.

### Manual Tests

GNOME Wayland:

```bash
echo $XDG_SESSION_TYPE
gdbus introspect --session \
  --dest org.freedesktop.portal.Desktop \
  --object-path /org/freedesktop/portal/desktop \
  | rg GlobalShortcuts
```

Then:

- Start TypeWhisper with `dotnet run`.
- Confirm backend status says portal or GNOME fallback.
- Press `Ctrl+Shift+Space`.
- Confirm recording starts/stops.
- Confirm restart preserves registration.

KDE Wayland:

- Confirm portal path first.
- If portal unavailable, inspect and test KGlobalAccel backend.

Hyprland:

- Confirm portal path first.
- If portal unavailable, confirm runtime bind appears and fires.

Unknown wlroots compositor:

- Confirm manual D-Bus command is shown and works when bound by the user.

X11:

- Confirm SharpHook still handles toggle, push-to-talk, and hybrid.

## Important Pitfalls

- Do not use X11 hooks on Wayland.
- Do not make GNOME the primary Wayland path.
- Do not bake HTTP bearer tokens into compositor shortcut commands.
- Do not depend on interactive shell `PATH` for desktop-launched commands.
- Do not claim push-to-talk works on a backend unless it emits key release or
  deactivation events reliably.
- Do not mark KDE fallback as complete without testing on KDE Plasma Wayland.
- Do not use `gdbus call` subprocesses for portal session ownership; portal
  sessions must be owned by the app process, not a short-lived helper process.

## Recommended Next Patch Scope

1. Add `Tmds.DBus.Protocol` to `TypeWhisper.Linux.csproj`.
2. Add `TypeWhisperDbusCallbackService` for fallback callback methods.
3. Add `IGlobalShortcutBackend` and backend result models.
4. Move SharpHook code behind `SharpHookX11ShortcutBackend`.
5. Add `XdgPortalGlobalShortcutsBackend`.
6. Keep existing GNOME/Hyprland code only as fallback, but change callback
   command to D-Bus.
7. Update Shortcuts UI to show backend state.
8. Update README after implementation is tested.

