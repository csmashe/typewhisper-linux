using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class MatchProfileCascadeTests : IDisposable
{
    private readonly string _filePath;
    private readonly ProfileService _sut;

    public MatchProfileCascadeTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new ProfileService(_filePath);
    }

    [Fact]
    public void AppAndWebsiteBeatsWebsiteOnlyAndAppOnly()
    {
        _sut.AddProfile(NewProfile("appAndSite", processNames: ["chrome"], urlPatterns: ["docs.google.com"], priority: 1));
        _sut.AddProfile(NewProfile("siteOnly", processNames: [], urlPatterns: ["docs.google.com"], priority: 100));
        _sut.AddProfile(NewProfile("appOnly", processNames: ["chrome"], urlPatterns: [], priority: 100));

        var result = _sut.MatchProfile("chrome", "https://docs.google.com/document/d/abc");

        Assert.Equal(MatchKind.AppAndWebsite, result.Kind);
        Assert.Equal("appAndSite", result.Profile?.Name);
        Assert.Equal("docs.google.com", result.MatchedDomain);
    }

    [Fact]
    public void WebsiteBeatsAppWhenAppAndWebsiteAbsent()
    {
        _sut.AddProfile(NewProfile("siteOnly", processNames: [], urlPatterns: ["docs.google.com"], priority: 1));
        _sut.AddProfile(NewProfile("appOnly", processNames: ["chrome"], urlPatterns: [], priority: 100));

        var result = _sut.MatchProfile("chrome", "https://docs.google.com/document/d/abc");

        Assert.Equal(MatchKind.Website, result.Kind);
        Assert.Equal("siteOnly", result.Profile?.Name);
    }

    [Fact]
    public void AppBeatsGlobalWhenWebsiteTiersEmpty()
    {
        _sut.AddProfile(NewProfile("global", processNames: [], urlPatterns: [], priority: 100));
        _sut.AddProfile(NewProfile("appOnly", processNames: ["chrome"], urlPatterns: [], priority: 1));

        var result = _sut.MatchProfile("chrome", url: null);

        Assert.Equal(MatchKind.App, result.Kind);
        Assert.Equal("appOnly", result.Profile?.Name);
    }

    [Fact]
    public void GlobalUsedWhenNothingElseMatches()
    {
        _sut.AddProfile(NewProfile("global", processNames: [], urlPatterns: [], priority: 0));

        var result = _sut.MatchProfile("notepad", url: null);

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.Equal("global", result.Profile?.Name);
    }

    [Fact]
    public void WonByPriorityFlagsUniqueWinnerOverLowerPriorityPeer()
    {
        _sut.AddProfile(NewProfile("low", processNames: ["chrome"], urlPatterns: [], priority: 1));
        _sut.AddProfile(NewProfile("high", processNames: ["chrome"], urlPatterns: [], priority: 50));

        var result = _sut.MatchProfile("chrome", url: null);

        Assert.Equal("high", result.Profile?.Name);
        Assert.Equal(1, result.CompetingProfileCount);
        Assert.True(result.WonByPriority);
    }

    [Fact]
    public void CompetingProfileCountReflectsPriorityTie()
    {
        _sut.AddProfile(NewProfile("a", processNames: ["chrome"], urlPatterns: [], priority: 50));
        _sut.AddProfile(NewProfile("b", processNames: ["chrome"], urlPatterns: [], priority: 50));

        var result = _sut.MatchProfile("chrome", url: null);

        Assert.Equal(MatchKind.App, result.Kind);
        Assert.Equal(2, result.CompetingProfileCount);
        Assert.False(result.WonByPriority);
    }

    [Fact]
    public void NoMatchReturnedWhenNothingApplies()
    {
        _sut.AddProfile(NewProfile("appOnly", processNames: ["chrome"], urlPatterns: [], priority: 0));

        var result = _sut.MatchProfile("notepad", url: null);

        Assert.Equal(MatchKind.NoMatch, result.Kind);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void ForcedProfileIdReturnsManualOverride()
    {
        var forced = NewProfile("forced", processNames: [], urlPatterns: [], priority: 0);
        _sut.AddProfile(forced);

        var result = _sut.MatchProfile("chrome", url: null, forcedProfileId: forced.Id);

        Assert.Equal(MatchKind.ManualOverride, result.Kind);
        Assert.Equal(forced.Id, result.Profile?.Id);
    }

    private static Profile NewProfile(
        string name,
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> urlPatterns,
        int priority) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsEnabled = true,
            Priority = priority,
            ProcessNames = processNames,
            UrlPatterns = urlPatterns
        };

    public void Dispose()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }
}
