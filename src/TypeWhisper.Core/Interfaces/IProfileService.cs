using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Stores per-app and per-site dictation profiles and resolves which one applies
///     to the active window.
/// </summary>
public interface IProfileService
{
    IReadOnlyList<Profile> Profiles { get; }

    void AddProfile(Profile profile);
    void UpdateProfile(Profile profile);
    void DeleteProfile(string id);

    /// <summary>
    ///     Atomically finds the latest profile with <paramref name="id" />, inverts its enabled
    ///     state, updates its timestamp, persists and publishes the complete list, and returns
    ///     the committed profile. Returns <see langword="null" /> without writing when missing.
    /// </summary>
    Profile? ToggleProfileEnabled(string id);

    /// <summary>Seeds the built-in default profiles only on a genuine first run (when no profile file exists yet).</summary>
    void SeedFirstRunDefaultsIfMissing();

    /// <summary>
    ///     Selects the best-matching enabled profile for the given process and/or URL.
    ///     <paramref name="forcedProfileId" /> requests a manual override, which still
    ///     falls through to normal matching when that profile is disabled.
    /// </summary>
    MatchResult MatchProfile(string? processName, string? url, string? forcedProfileId = null);

    event Action? ProfilesChanged;
}
