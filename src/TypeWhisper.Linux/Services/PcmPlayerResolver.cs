namespace TypeWhisper.Linux.Services;

internal enum PcmPlayerKind
{
    PwPlay,
    Paplay,
    Aplay,
}

internal sealed record ResolvedPcmPlayer(PcmPlayerKind Kind, string AbsolutePath);

internal static class PcmPlayerResolver
{
    private const UnixFileMode ExecutableModeMask =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static readonly (string Name, PcmPlayerKind Kind)[] s_candidates =
    [
        ("pw-play", PcmPlayerKind.PwPlay),
        ("paplay", PcmPlayerKind.Paplay),
        ("aplay", PcmPlayerKind.Aplay),
    ];

    internal static ResolvedPcmPlayer? Resolve()
    {
        return Resolve(Environment.GetEnvironmentVariable("PATH"));
    }

    internal static ResolvedPcmPlayer? Resolve(string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var directories = pathValue.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        foreach (var (name, kind) in s_candidates)
        {
            foreach (var directory in directories)
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Join(directory, name));
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
                    if (
                        File.Exists(candidate)
                        && (File.GetUnixFileMode(candidate) & ExecutableModeMask) != 0
                    )
#pragma warning restore CA1416
                    {
                        return new ResolvedPcmPlayer(kind, candidate);
                    }
                }
                catch
                {
                    // Invalid, inaccessible, or stale PATH entry: continue discovery.
                }
            }
        }

        return null;
    }
}
