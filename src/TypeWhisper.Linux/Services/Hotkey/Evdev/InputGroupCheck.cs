using System.Diagnostics;
using System.IO;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
/// Best-effort check of whether the current user belongs to the
/// <c>input</c> group on Linux. Used to surface the "add yourself to the
/// input group" banner only when the fix is actually applicable. Returns
/// <see langword="null"/> when membership can't be determined — callers
/// should treat that as "don't show the banner; we can't tell".
/// </summary>
public static class InputGroupCheck
{
    private const string InputGroupName = "input";

    public static bool? CurrentUserInInputGroup()
    {
        var inputGid = ReadInputGid();
        if (inputGid is null) return null;

        var groups = ReadCurrentGroups();
        if (groups is null) return null;

        return groups.Contains(inputGid.Value);
    }

    private static int? ReadInputGid()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/group"))
            {
                // /etc/group lines look like: input:x:104:user1,user2
                var firstColon = line.IndexOf(':');
                if (firstColon <= 0) continue;
                if (!line.AsSpan(0, firstColon).SequenceEqual(InputGroupName)) continue;

                var afterName = line.AsSpan(firstColon + 1);
                var afterX = afterName.IndexOf(':');
                if (afterX < 0) continue;
                var gidStart = afterX + 1;
                var rest = afterName[gidStart..];
                var nextColon = rest.IndexOf(':');
                var gidSlice = nextColon < 0 ? rest : rest[..nextColon];
                if (int.TryParse(gidSlice, out var gid)) return gid;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InputGroupCheck] Read /etc/group failed: {ex.Message}");
        }
        return null;
    }

    private static HashSet<int>? ReadCurrentGroups()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                // Looking for: Groups:	4 24 27 30 46 116 1000
                if (!line.StartsWith("Groups:", StringComparison.Ordinal)) continue;
                var rest = line.AsSpan("Groups:".Length).TrimStart();
                var result = new HashSet<int>();
                foreach (var range in TokenizeWhitespace(rest))
                {
                    if (int.TryParse(range, out var gid)) result.Add(gid);
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[InputGroupCheck] Read /proc/self/status failed: {ex.Message}");
        }
        return null;
    }

    private static IEnumerable<string> TokenizeWhitespace(ReadOnlySpan<char> span)
    {
        var result = new List<string>();
        int i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            var start = i;
            while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
            if (i > start) result.Add(span[start..i].ToString());
        }
        return result;
    }
}
