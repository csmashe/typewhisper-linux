using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class ProfileService : IProfileService
{
    private readonly string _filePath;
    private List<Profile> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<Profile> Profiles
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? ProfilesChanged;

    public ProfileService(string filePath)
    {
        _filePath = filePath;
    }

    public void AddProfile(Profile profile)
    {
        EnsureCacheLoaded();
        _cache.Add(profile);
        SortCache();
        SaveToDisk();
        ProfilesChanged?.Invoke();
    }

    public void UpdateProfile(Profile profile)
    {
        EnsureCacheLoaded();
        var updated = profile with { UpdatedAt = DateTime.UtcNow };
        var idx = _cache.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0) _cache[idx] = updated;
        SortCache();
        SaveToDisk();
        ProfilesChanged?.Invoke();
    }

    public void DeleteProfile(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(p => p.Id == id);
        SaveToDisk();
        ProfilesChanged?.Invoke();
    }

    public MatchResult MatchProfile(string? processName, string? url, string? forcedProfileId = null)
    {
        EnsureCacheLoaded();

        if (forcedProfileId is not null)
        {
            var forced = _cache.FirstOrDefault(p => p.Id == forcedProfileId);
            if (forced is not null)
                return new MatchResult(forced, MatchKind.ManualOverride, null, 1, false);
        }

        var enabled = _cache.Where(p => p.IsEnabled).ToList();
        var host = url is not null ? ExtractHost(url) : null;

        var appAndWebsite = new List<(Profile Profile, string? MatchedPattern)>();
        var websiteOnly = new List<(Profile Profile, string? MatchedPattern)>();
        var appOnly = new List<Profile>();
        var global = new List<Profile>();

        foreach (var profile in enabled)
        {
            var processMatches = processName is not null
                && profile.ProcessNames.Count > 0
                && profile.ProcessNames.Any(pn =>
                    processName.Equals(pn, StringComparison.OrdinalIgnoreCase));

            string? urlMatchPattern = null;
            if (url is not null && profile.UrlPatterns.Count > 0)
            {
                urlMatchPattern = profile.UrlPatterns.FirstOrDefault(pattern =>
                    host is not null && MatchesUrlPattern(host, url, pattern));
            }

            if (processMatches && urlMatchPattern is not null)
                appAndWebsite.Add((profile, urlMatchPattern));
            else if (urlMatchPattern is not null && profile.ProcessNames.Count == 0)
                websiteOnly.Add((profile, urlMatchPattern));
            else if (processMatches && profile.UrlPatterns.Count == 0)
                appOnly.Add(profile);
            else if (profile.ProcessNames.Count == 0 && profile.UrlPatterns.Count == 0)
                global.Add(profile);
        }

        if (appAndWebsite.Count > 0)
            return BuildResult(appAndWebsite, MatchKind.AppAndWebsite, includeDomain: true);
        if (websiteOnly.Count > 0)
            return BuildResult(websiteOnly, MatchKind.Website, includeDomain: true);
        if (appOnly.Count > 0)
            return BuildResult(appOnly.Select(p => (p, (string?)null)).ToList(), MatchKind.App, includeDomain: false);
        if (global.Count > 0)
            return BuildResult(global.Select(p => (p, (string?)null)).ToList(), MatchKind.Global, includeDomain: false);

        return MatchResult.NoMatch;
    }

    private static MatchResult BuildResult(
        List<(Profile Profile, string? MatchedPattern)> tier,
        MatchKind kind,
        bool includeDomain)
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
                return uri.Host;
        }
        catch { }
        return null;
    }

    private static bool MatchesUrlPattern(string host, string url, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            var suffix = pattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                   || host.Equals(pattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
    }

    private void SortCache()
    {
        _cache.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

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

        SortCache();
        _cacheLoaded = true;
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
