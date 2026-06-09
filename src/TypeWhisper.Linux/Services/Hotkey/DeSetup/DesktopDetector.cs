namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Cheap, environment-variable based detection of the current desktop
///     environment. Used both by the UI (to pick which writer's button to
///     show) and by writers themselves (so each writer doesn't have to
///     re-parse <c>XDG_CURRENT_DESKTOP</c>).
///     Detection is intentionally shallow — we only care about the four
///     desktops we actually have writers for. Anything else is "unknown",
///     which the UI surfaces as the generic "copy this command" path.
/// </summary>
public static class DesktopDetector
{
    /// <summary>Stable token for unknown / unsupported desktops.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    ///     Returns one of "gnome", "kde", "hyprland", "sway", or "unknown".
    ///     Order of checks: the session-signature env vars (Hyprland's
    ///     <c>HYPRLAND_INSTANCE_SIGNATURE</c>, Sway's <c>SWAYSOCK</c>) win
    ///     over <c>XDG_CURRENT_DESKTOP</c> because users sometimes start
    ///     Hyprland inside a host session that already set the XDG var.
    /// </summary>
    public static string DetectId()
    {
        if (
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"))
        )
        {
            return "hyprland";
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK")))
        {
            return "sway";
        }

        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Unknown;
        }

        // XDG_CURRENT_DESKTOP can be colon-separated like "ubuntu:GNOME".
        // We lower-case the whole thing and look for substrings — that
        // handles "GNOME", "ubuntu:GNOME", "X-Cinnamon", "KDE" with
        // identical code.
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("hyprland"))
        {
            return "hyprland";
        }

        if (lower.Contains("sway"))
        {
            return "sway";
        }

        if (lower.Contains("gnome") || lower.Contains("ubuntu"))
        {
            return "gnome";
        }

        if (lower.Contains("kde") || lower.Contains("plasma"))
        {
            return "kde";
        }

        return Unknown;
    }

    /// <summary>
    ///     True on tiling window managers (Hyprland, Sway, River, Niri), where
    ///     the floating dictation overlay misbehaves — it reserves a tile,
    ///     steals focus from the dictation target, and blurs into a box. On
    ///     those we surface "recording" with a desktop notification instead of
    ///     the overlay. Full desktop environments — GNOME, KDE, Cinnamon, XFCE,
    ///     … — and anything unrecognized keep the overlay, which behaves well
    ///     there. Conservative by design: only a known tiling WM opts into the
    ///     notification path, so an untested environment never loses its overlay.
    /// </summary>
    public static bool UsesNotificationRecordingIndicator()
    {
        if (DetectId() is "hyprland" or "sway")
        {
            return true;
        }

        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var lower = raw.ToLowerInvariant();
        return lower.Contains("river") || lower.Contains("niri");
    }

    /// <summary>
    ///     Display-name mapping for the detected ID. Falls back to the raw
    ///     XDG token if we don't recognize the desktop — keeps the status
    ///     panel readable on XFCE / Cinnamon / etc. without forcing them
    ///     through "unknown".
    /// </summary>
    public static string DisplayName(string? id = null)
    {
        var resolved = id ?? DetectId();
        return resolved switch
        {
            "gnome" => "GNOME",
            "kde" => "KDE Plasma",
            "hyprland" => "Hyprland",
            "sway" => "Sway",
            _ => RawXdgFallback()
        };
    }

    /// <summary>
    ///     True if a binary with the given name is reachable through
    ///     <c>PATH</c>. Used by writers to verify their helper command
    ///     (e.g. <c>gsettings</c>, <c>hyprctl</c>, <c>swaymsg</c>) is
    ///     actually installed before claiming to support the desktop.
    /// </summary>
    public static bool BinaryExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
            catch
            {
                // Bad PATH entry — skip.
            }
        }

        return false;
    }

    private static string RawXdgFallback()
    {
        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "your desktop";
        }

        var tokens = raw.Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (tokens.Length == 0)
        {
            return "your desktop";
        }

        return tokens[^1] switch
        {
            "GNOME" => "GNOME",
            "ubuntu" => "GNOME",
            "KDE" => "KDE Plasma",
            "Hyprland" => "Hyprland",
            "sway" => "Sway",
            "XFCE" => "XFCE",
            "MATE" => "MATE",
            "Cinnamon" => "Cinnamon",
            "Unity" => "Unity",
            "LXQt" => "LXQt",
            "Pantheon" => "Pantheon",
            "Budgie" => "Budgie",
            "Deepin" => "Deepin",
            _ => tokens[^1]
        };
    }
}