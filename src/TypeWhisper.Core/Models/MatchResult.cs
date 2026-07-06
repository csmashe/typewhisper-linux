namespace TypeWhisper.Core.Models;

/// <summary>
///     Outcome of the profile-matching pass against the current active window.
///     <see cref="Kind" /> records which signal matched (most specific wins:
///     AppAndWebsite > Website > App > Global), <see cref="MatchedDomain" /> is
///     set only for URL-scoped matches, and <see cref="WonByPriority" /> flags
///     the case where multiple profiles competed at the same kind and the
///     user-set priority broke the tie — surfaced so the UI can hint "matched
///     by priority" instead of looking arbitrary.
/// </summary>
public sealed record MatchResult(
    Profile? Profile,
    MatchKind Kind,
    string? MatchedDomain,
    int CompetingProfileCount,
    bool WonByPriority
)
{
    public static readonly MatchResult NoMatch = new(null, MatchKind.NoMatch, null, 0, false);
}