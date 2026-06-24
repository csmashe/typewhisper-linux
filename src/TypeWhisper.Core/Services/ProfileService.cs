using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class ProfileService : IProfileService
{
    private readonly string _filePath;
    private List<Profile> _cache = [];
    private bool _cacheLoaded;

    public ProfileService(string filePath)
    {
        _filePath = filePath;
    }

    public IReadOnlyList<Profile> Profiles
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? ProfilesChanged;

    public void SeedFirstRunDefaultsIfMissing()
    {
        // Seed only when the file has never been written; if the user later deletes
        // the seeded profile the file still exists, so we never resurrect it.
        if (File.Exists(_filePath))
        {
            return;
        }

        EnsureCacheLoaded();
        if (_cache.Any(p => p.Id == FirstRunDefaults.AutoFormatProfileId))
        {
            return;
        }

        var newCache = new List<Profile>(_cache) { FirstRunDefaults.CreateAutoFormatProfile() };
        SortList(newCache);
        SaveToDisk(newCache);
        _cache = newCache;
        ProfilesChanged?.Invoke();
    }

    public void AddProfile(Profile profile)
    {
        EnsureCacheLoaded();
        // Persist before swapping _cache so a save failure can't leave the service
        // holding an unsaved profile that a later successful save would silently flush.
        var newCache = new List<Profile>(_cache) { profile };
        SortList(newCache);
        SaveToDisk(newCache);
        _cache = newCache;
        ProfilesChanged?.Invoke();
    }

    public void UpdateProfile(Profile profile)
    {
        EnsureCacheLoaded();
        var updated = profile with { UpdatedAt = DateTime.UtcNow };
        var newCache = new List<Profile>(_cache);
        var idx = newCache.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0)
        {
            newCache[idx] = updated;
        }

        SortList(newCache);
        SaveToDisk(newCache);
        _cache = newCache;
        ProfilesChanged?.Invoke();
    }

    public void DeleteProfile(string id)
    {
        EnsureCacheLoaded();
        var newCache = new List<Profile>(_cache);
        newCache.RemoveAll(p => p.Id == id);
        SaveToDisk(newCache);
        _cache = newCache;
        ProfilesChanged?.Invoke();
    }

    public MatchResult MatchProfile(
        string? processName,
        string? url,
        string? forcedProfileId = null
    )
    {
        EnsureCacheLoaded();

        if (forcedProfileId is not null)
        {
            // A forced selection pointing at a disabled profile should still fall through —
            // don't activate a profile the user has explicitly turned off.
            var forced = _cache.FirstOrDefault(p => p.Id == forcedProfileId && p.IsEnabled);
            if (forced is not null)
            {
                return new MatchResult(forced, MatchKind.ManualOverride, null, 1, false);
            }
        }

        var enabled = _cache.Where(p => p.IsEnabled).ToList();
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
                    host is not null && MatchesUrlPattern(host, url, pattern)
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
        catch { }

        return null;
    }

    private static bool MatchesUrlPattern(string host, string url, string pattern)
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

    private static void SortList(List<Profile> profiles)
    {
        profiles.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonSerializer.Deserialize<List<Profile>>(json) ?? [];
            }
        }
        catch
        {
            _cache = [];
        }

        SortList(_cache);
        _cacheLoaded = true;
    }

    private void SaveToDisk(IReadOnlyList<Profile> profiles)
    {
        // Atomic write-then-rename: a mid-write crash previously truncated _filePath,
        // which EnsureCacheLoaded would silently discard, losing all saved profiles.
        string? tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(
                profiles,
                new JsonSerializerOptions { WriteIndented = true }
            );

            tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }

            tempPath = null;
        }
        finally
        {
            // Surface persistence failures: swallowing them left _cache mutated
            // and ProfilesChanged firing as if the write had succeeded.
            if (tempPath is not null)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }
            }
        }
    }
}