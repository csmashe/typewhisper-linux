namespace TypeWhisper.Core.Models;

public sealed record MatchResult(
    Profile? Profile,
    MatchKind Kind,
    string? MatchedDomain,
    int CompetingProfileCount,
    bool WonByPriority)
{
    public static readonly MatchResult NoMatch = new(null, MatchKind.NoMatch, null, 0, false);
}

public enum MatchKind
{
    AppAndWebsite,
    Website,
    App,
    Global,
    ManualOverride,
    NoMatch
}
