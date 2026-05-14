namespace TypeWhisper.Linux.Services;

internal static class ProcessNameNormalizer
{
    public static string Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return "";

        var baseName = Path.GetFileName(processName.Trim());
        if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(baseName);

        // Wayland wm_class / app_id values are commonly reverse-DNS app IDs:
        //   com.mitchellh.ghostty, org.mozilla.firefox, org.gnome.Nautilus
        // Downstream heuristics (terminal/browser detection, profile-process
        // matching) expect the canonical short name — "ghostty", "firefox",
        // "nautilus" — so collapse 3+ segment DNS forms to the last segment.
        // We require >=2 dots so a stray two-token name like "chrome.app"
        // doesn't get mangled into "app".
        if (baseName.Count(c => c == '.') >= 2)
        {
            var lastSegment = baseName[(baseName.LastIndexOf('.') + 1)..];
            if (!string.IsNullOrEmpty(lastSegment))
                return lastSegment;
        }

        return baseName;
    }
}
