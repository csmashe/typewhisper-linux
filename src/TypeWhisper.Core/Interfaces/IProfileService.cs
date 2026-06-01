using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IProfileService
{
    IReadOnlyList<Profile> Profiles { get; }

    void AddProfile(Profile profile);
    void UpdateProfile(Profile profile);
    void DeleteProfile(string id);
    void SeedFirstRunDefaultsIfMissing();
    MatchResult MatchProfile(string? processName, string? url, string? forcedProfileId = null);
    event Action? ProfilesChanged;
}