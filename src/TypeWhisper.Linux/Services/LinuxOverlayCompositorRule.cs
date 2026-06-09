using System.Text.Json;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     On tiling Wayland compositors (Hyprland, Sway) a plain xdg-toplevel is
///     tiled by default and can grab focus. The dictation overlay must never do
///     either: tiling would rearrange the user's windows every time it shows,
///     and stealing focus would send the dictated keystrokes (and the wizard's
///     paste test) into the overlay instead of the target window. This service
///     registers compositor window-rules — matched on the overlay's unique
///     title — that float it, pin it as a HUD, keep it from taking focus, and
///     suppress the compositor's blur/shadow chrome around our own surface.
///     <para>
///         No-op on floating shells (GNOME/KDE) — they already float utility
///         windows — and on any session without <c>hyprctl</c>/<c>swaymsg</c>.
///         Runtime-only: nothing is written to the user's config, so there is
///         nothing to uninstall; the app re-registers the rules on every start.
///         The rules are declarative and persist for the compositor session, so
///         they apply on every map — including each time the overlay is shown
///         after being hidden idle (see <see cref="Views.DictationOverlayWindow" />).
///     </para>
/// </summary>
public sealed class LinuxOverlayCompositorRule
{
    /// <summary>
    ///     Unique xdg-toplevel title set on <see cref="Views.DictationOverlayWindow" />
    ///     so the rules match only the overlay — never the main window, nor the
    ///     other Avalonia windows that default to the title "Window".
    /// </summary>
    public const string OverlayWindowTitle = "TypeWhisper Overlay";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);

    private readonly IProcessRunner _runner;

    public LinuxOverlayCompositorRule(IProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    ///     Register the overlay window-rules for the current compositor. Safe to
    ///     fire-and-forget at startup: the rules are declarative and apply on
    ///     every subsequent map of the overlay. Every failure is swallowed
    ///     (missing binary, dead IPC socket, non-tiling shell) — the overlay
    ///     just behaves as it did before.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        try
        {
            switch (DesktopDetector.DetectId())
            {
                case "hyprland":
                    await ApplyHyprlandAsync(ct).ConfigureAwait(false);
                    break;
                case "sway":
                    await ApplySwayAsync(ct).ConfigureAwait(false);
                    break;
            }
        }
        catch
        {
            // Purely cosmetic — never let it disrupt startup.
        }
    }

    private async Task ApplyHyprlandAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("hyprctl"))
        {
            return;
        }

        // Declarative rules cover any future re-map and the nofocus/noblur/
        // noshadow chrome (these have no per-window dispatch equivalent).
        string[] rules =
        {
            $"float, title:^({OverlayWindowTitle})$",
            $"nofocus, title:^({OverlayWindowTitle})$",
            $"pin, title:^({OverlayWindowTitle})$",
            $"noblur, title:^({OverlayWindowTitle})$",
            $"noshadow, title:^({OverlayWindowTitle})$",
        };
        foreach (var rule in rules)
        {
            await _runner
                .RunAsync(
                    "hyprctl",
                    new[] { "keyword", "windowrulev2", rule },
                    timeout: CallTimeout,
                    ct: ct
                )
                .ConfigureAwait(false);
        }

        // The declarative float above is matched on title, but Avalonia sets the
        // xdg/X11 title AFTER the surface maps — so on the overlay's startup map
        // the rule misses and Hyprland tiles it (reserving a slot). The reliable
        // path: find the now-titled window by address and float + pin it, then
        // park it off-screen so the always-mapped idle surface is neither a box
        // nor a click-trap (it's brought back on-screen by the window itself
        // when a dictation starts).
        var address = await PollForOverlayAddressAsync(ct).ConfigureAwait(false);
        if (address is null)
        {
            return;
        }

        foreach (var dispatch in new[]
                 {
                     new[] { "dispatch", "setfloating", $"address:{address}" },
                     new[] { "dispatch", "pin", $"address:{address}" },
                 })
        {
            await _runner
                .RunAsync("hyprctl", dispatch, timeout: CallTimeout, ct: ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Poll <c>hyprctl clients -j</c> for the overlay's address, keyed on its
    ///     unique title. Retries for ~2s so it tolerates being called right after
    ///     the window is shown but before it finishes mapping. Null if not found.
    /// </summary>
    private async Task<string?> PollForOverlayAddressAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await _runner
                .RunAsync("hyprctl", new[] { "clients", "-j" }, timeout: CallTimeout, ct: ct)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                var address = FindAddressByTitle(result.StandardOutput, OverlayWindowTitle);
                if (address is not null)
                {
                    return address;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        return null;
    }

    private static string? FindAddressByTitle(string clientsJson, string title)
    {
        try
        {
            using var doc = JsonDocument.Parse(clientsJson);
            foreach (var client in doc.RootElement.EnumerateArray())
            {
                if (
                    client.TryGetProperty("title", out var t)
                    && t.GetString() == title
                    && client.TryGetProperty("address", out var a)
                )
                {
                    return a.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed output — treat as "not found" and let the caller retry.
        }

        return null;
    }

    private async Task ApplySwayAsync(CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("swaymsg"))
        {
            return;
        }

        // Sway evaluates for_window criteria as each window maps, so this floats
        // the overlay and drops its border on every show. The whole command is a
        // single argument that swaymsg parses itself. (Sway has no per-window
        // "never focus" rule; ShowActivated=false on the window covers it.)
        await _runner
            .RunAsync(
                "swaymsg",
                new[] { $"for_window [title=\"{OverlayWindowTitle}\"] floating enable, border none" },
                timeout: CallTimeout,
                ct: ct
            )
            .ConfigureAwait(false);
    }
}
