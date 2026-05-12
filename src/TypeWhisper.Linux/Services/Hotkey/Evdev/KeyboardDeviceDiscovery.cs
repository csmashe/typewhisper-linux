using System.Diagnostics;
using System.IO;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
/// Enumerates keyboard <c>/dev/input/eventN</c> nodes via the by-path
/// symlinks. We rely on udev's naming convention — every kernel-recognized
/// keyboard surfaces as <c>/dev/input/by-path/*-event-kbd</c>. The fallback
/// of probing every event* node with <c>ioctl(EVIOCGBIT)</c> is deferred
/// until we see real users on systems where the symlinks are absent.
/// </summary>
internal static class KeyboardDeviceDiscovery
{
    private const string ByPathDir = "/dev/input/by-path";

    public static IReadOnlyList<string> EnumerateKeyboards()
    {
        if (!Directory.Exists(ByPathDir)) return Array.Empty<string>();

        var result = new List<string>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(ByPathDir);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[KeyboardDeviceDiscovery] Enumerate threw: {ex.Message}");
            return Array.Empty<string>();
        }

        foreach (var entry in entries)
        {
            if (!entry.EndsWith("-event-kbd", StringComparison.Ordinal)) continue;
            var resolved = ResolveSymlink(entry);
            if (resolved is null) continue;
            if (File.Exists(resolved)) result.Add(resolved);
        }

        return result;
    }

    public static string? ResolveSymlink(string path)
    {
        try
        {
            // GetLinkTarget returns the immediate target; for a relative
            // symlink like "../event3" we resolve it against the directory
            // holding the link.
            var info = new FileInfo(path);
            var target = info.LinkTarget;
            if (target is null) return path;
            if (Path.IsPathRooted(target)) return target;
            var dir = Path.GetDirectoryName(path);
            return dir is null ? target : Path.GetFullPath(Path.Combine(dir, target));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[KeyboardDeviceDiscovery] ResolveSymlink({path}) threw: {ex.Message}");
            return null;
        }
    }
}
