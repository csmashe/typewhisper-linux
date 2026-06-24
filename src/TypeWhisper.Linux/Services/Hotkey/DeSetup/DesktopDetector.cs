namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Env-var based detection of the current desktop. Detection is shallow
///     by design — only the four desktops with writers are recognised;
///     anything else is "unknown" and surfaces the generic "copy this command" path.
/// </summary>
public static class DesktopDetector
{
    /// <summary>Stable token for unknown / unsupported desktops.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    ///     Returns one of "gnome", "kde", "hyprland", "sway", or "unknown".
    ///     Session-signature env vars (<c>HYPRLAND_INSTANCE_SIGNATURE</c>,
    ///     <c>SWAYSOCK</c>) take priority over <c>XDG_CURRENT_DESKTOP</c>
    ///     because Hyprland can be nested inside a host session that already set it.
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

        // XDG_CURRENT_DESKTOP can be colon-separated (e.g. "ubuntu:GNOME").
        // Substring-matching on the lowercased value handles all known variants.
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
    ///     True on tiling WMs (Hyprland, Sway, River, Niri), where the floating
    ///     overlay reserves a tile, steals focus, and blurs into a box — so those
    ///     use a desktop notification instead. Conservative: only known tiling WMs
    ///     opt in; unrecognised environments keep the overlay.
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
    ///     Display-name for the detected (or given) ID. Falls back to the raw
    ///     XDG token so XFCE, Cinnamon, etc. remain readable instead of "unknown".
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
    ///     True if the named binary is reachable via <c>PATH</c>. Used by
    ///     writers to confirm their helper tool (gsettings, hyprctl, swaymsg…)
    ///     is installed before claiming desktop support.
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
                var candidate = Path.Join(dir, name);
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