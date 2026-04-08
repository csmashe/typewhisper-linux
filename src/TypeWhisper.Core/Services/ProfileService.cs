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

    public Profile? MatchProfile(string? processName, string? url)
    {
        EnsureCacheLoaded();

        foreach (var profile in _cache.Where(p => p.IsEnabled))
        {
            if (processName is not null && profile.ProcessNames.Count > 0)
            {
                if (profile.ProcessNames.Any(pn =>
                    processName.Equals(pn, StringComparison.OrdinalIgnoreCase)))
                    return profile;
            }

            if (url is not null && profile.UrlPatterns.Count > 0)
            {
                var host = ExtractHost(url);
                if (host is not null && profile.UrlPatterns.Any(pattern =>
                    MatchesUrlPattern(host, url, pattern)))
                    return profile;
            }
        }

        return null;
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
