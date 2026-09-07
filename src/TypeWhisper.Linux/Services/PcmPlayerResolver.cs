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
        // Candidate order, not PATH order, decides: pw-play anywhere on PATH beats an aplay
        // that happens to sit in an earlier directory.
        foreach (var (name, kind) in s_candidates)
        {
            if (ExecutablePathResolver.Find(name, pathValue) is { } absolutePath)
            {
                return new ResolvedPcmPlayer(kind, absolutePath);
            }
        }

        return null;
    }
}
