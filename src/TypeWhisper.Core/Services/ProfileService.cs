using System.Collections.Immutable;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="IProfileService" />: persists dictation profiles as JSON and resolves
///     which profile matches the active window by app name, URL pattern, or global fallback.
/// </summary>
public sealed class ProfileService : IProfileService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly AtomicJsonStore<ImmutableArray<Profile>> _store;

    public ProfileService(string filePath)
        : this(filePath, AtomicFileWrite.WriteAllText) { }

    internal ProfileService(string filePath, Action<string, string>? atomicWrite)
    {
        _filePath = Path.GetFullPath(filePath);
        var options = new AtomicJsonStoreOptions<ImmutableArray<Profile>>
        {
            JsonOptions = s_jsonOptions,
            CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
            Deserialize = json =>
            {
                var profiles = JsonSerializer.Deserialize<ImmutableArray<Profile>>(
                    json,
                    s_jsonOptions
                );
                return profiles.IsDefault
                    ? throw new JsonException("Profile JSON deserialized to null.")
                    : Sort(profiles);
            },
        };
        _store = new AtomicJsonStore<ImmutableArray<Profile>>(
            _filePath,
            static () => [],
            options,
            atomicWrite ?? AtomicFileWrite.WriteAllText
        );
    }

    public IReadOnlyList<Profile> Profiles => _store.Current.ToArray();

    public event Action? ProfilesChanged;

    public void SeedFirstRunDefaultsIfMissing()
    {
        // Seed only when the file has never been written; if the user later deletes
        // the seeded profile the file still exists, so we never resurrect it.
        if (File.Exists(_filePath))
        {
            return;
        }

        Commit(
            current =>
                current.Any(p => p.Id == FirstRunDefaults.AutoFormatProfileId)
                    ? current
                    : current.Add(FirstRunDefaults.CreateAutoFormatProfile())
        );
    }

    public void AddProfile(Profile profile)
    {
        Commit(current => current.Add(profile));
    }

    public void UpdateProfile(Profile profile)
    {
        Commit(
            current =>
            {
                var updated = profile with { UpdatedAt = DateTime.UtcNow };
                var idx = FindIndex(current, p => p.Id == profile.Id);
                return idx < 0 ? current : current.SetItem(idx, updated);
            }
        );
    }

    public void DeleteProfile(string id)
    {
        Commit(
            current =>
            {
                var next = current.Where(p => p.Id != id).ToImmutableArray();
                return next.Length == current.Length ? current : next;
            }
        );
    }

    public Profile? ToggleProfileEnabled(string id)
    {
        Profile? updated = null;
        Commit(
            current =>
            {
                var idx = FindIndex(current, profile => profile.Id == id);
                if (idx < 0)
                {
                    return current;
                }

                updated = current[idx] with
                {
                    IsEnabled = !current[idx].IsEnabled,
                UpdatedAt = DateTime.UtcNow,
                };
                return current.SetItem(idx, updated);
            }
        );
        return updated;
    }

    public MatchResult MatchProfile(
        string? processName,
        string? url,
        string? forcedProfileId = null
    )
    {
        return MatchProfile(_store.Current, processName, url, forcedProfileId);
    }

    private static MatchResult MatchProfile(
        ImmutableArray<Profile> profiles,
        string? processName,
        string? url,
        string? forcedProfileId
    )
    {
        if (forcedProfileId is not null)
        {
            // A forced selection pointing at a disabled profile should still fall through —
            // don't activate a profile the user has explicitly turned off.
            var forced = profiles.FirstOrDefault(p => p.Id == forcedProfileId && p.IsEnabled);
            if (forced is not null)
            {
                return new MatchResult(forced, MatchKind.ManualOverride, null, 1, false);
            }
        }

        var enabled = profiles.Where(p => p.IsEnabled).ToList();
        var host = url is not null ? ExtractHost(url) : null;

        var appAndWebsite = new List<(Profile Profile, string? MatchedPattern)>();
        var websiteOnly = new List<(Profile Profile, string? MatchedPattern)>();
        var appOnly = new List<Profile>();
        var global = new List<Profile>();

        foreach (var profile in enabled)
        {
            var processMatches =
                processName is not null
                && profile.ProcessNames.Count > 0
                && profile.ProcessNames.Any(pn =>
                    processName.Equals(pn, StringComparison.OrdinalIgnoreCase)
                );

            string? urlMatchPattern = null;
            if (url is not null && profile.UrlPatterns.Count > 0)
            {
                urlMatchPattern = profile.UrlPatterns.FirstOrDefault(pattern =>
                    host is not null && MatchesUrlPattern(host, pattern)
                );
            }

            if (processMatches && urlMatchPattern is not null)
            {
                appAndWebsite.Add((profile, urlMatchPattern));
            }
            else if (urlMatchPattern is not null && profile.ProcessNames.Count == 0)
            {
                websiteOnly.Add((profile, urlMatchPattern));
            }
            else if (processMatches && profile.UrlPatterns.Count == 0)
            {
                appOnly.Add(profile);
            }
            else if (
                profile.ProcessNames.Count == 0
                && profile.UrlPatterns.Count == 0
                && string.IsNullOrWhiteSpace(profile.HotkeyData)
            )
            {
                // No app/URL matchers = global fallback, UNLESS it also has a hotkey,
                // which makes it a hotkey-only profile (explicit trigger, not "match everything").
                // Exclude it here so it never hijacks plain dictation; it's still reachable
                // via forcedProfileId when its chord is pressed.
                global.Add(profile);
            }
        }

        if (appAndWebsite.Count > 0)
        {
            return BuildResult(appAndWebsite, MatchKind.AppAndWebsite, true);
        }

        if (websiteOnly.Count > 0)
        {
            return BuildResult(websiteOnly, MatchKind.Website, true);
        }

        if (appOnly.Count > 0)
        {
            return BuildResult(
                appOnly.Select(p => (p, (string?)null)).ToList(),
                MatchKind.App,
                false
            );
        }

        if (global.Count > 0)
        {
            return BuildResult(
                global.Select(p => (p, (string?)null)).ToList(),
                MatchKind.Global,
                false
            );
        }

        return MatchResult.NoMatch;
    }

    private static MatchResult BuildResult(
        List<(Profile Profile, string? MatchedPattern)> tier,
        MatchKind kind,
        bool includeDomain
    )
    {
        var maxPriority = tier.Max(t => t.Profile.Priority);
        var top = tier.Where(t => t.Profile.Priority == maxPriority).ToList();
        var competing = top.Count;
        var hasLowerPriority = tier.Any(t => t.Profile.Priority < maxPriority);
        var winner = top[0];
        var matchedDomain = includeDomain ? winner.MatchedPattern : null;
        var wonByPriority = competing == 1 && hasLowerPriority;
        return new MatchResult(winner.Profile, kind, matchedDomain, competing, wonByPriority);
    }

    private static string? ExtractHost(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }
        }
        catch
        {
            // Malformed URL → treat as having no host.
        }

        return null;
    }

    private static bool MatchesUrlPattern(string host, string pattern)
    {
        if (!pattern.StartsWith("*."))
        {
            return host.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                   || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
        }

        // *.example.com matches sub.example.com AND example.com (bare apex)
        var suffix = pattern[1..]; // includes the leading dot, e.g. ".example.com"
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               || host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);

        // Plain pattern (e.g. "example.com") also matches any subdomain
    }

    private static ImmutableArray<Profile> Sort(ImmutableArray<Profile> profiles)
    {
        for (var index = 1; index < profiles.Length; index++)
        {
            if (profiles[index - 1].Priority < profiles[index].Priority)
            {
                return [.. profiles.OrderByDescending(p => p.Priority)];
            }
        }

        return profiles;
    }

    private void Commit(Func<ImmutableArray<Profile>, ImmutableArray<Profile>> update)
    {
        var changed = false;
        _store.Update(
            current =>
            {
                var next = Sort(update(current));
                changed = !next.Equals(current);
                return next;
            }
        );
        if (changed)
        {
            ProfilesChanged?.Invoke();
        }
    }

    private static int FindIndex(
        ImmutableArray<Profile> profiles,
        Func<Profile, bool> predicate
    )
    {
        for (var index = 0; index < profiles.Length; index++)
        {
            if (predicate(profiles[index]))
            {
                return index;
            }
        }
        return -1;
    }
}
