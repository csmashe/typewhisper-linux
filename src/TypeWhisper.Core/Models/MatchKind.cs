namespace TypeWhisper.Core.Models;

/// <summary>Which signal matched a profile, listed most-specific first (app+website beats website beats app beats global).</summary>
public enum MatchKind
{
    AppAndWebsite,
    Website,
    App,
    Global,
    ManualOverride,
    NoMatch
}
