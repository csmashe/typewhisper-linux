using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Guards <see cref="ProfileService" />'s profile-match cascade: app/website/global tiers, priority ties, and manual override.</summary>
public sealed class MatchProfileCascadeTests : IDisposable
{
    private readonly string _filePath;
    private readonly ProfileService _sut;

    public MatchProfileCascadeTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new ProfileService(_filePath);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    [Fact]
    public void AppAndWebsiteBeatsWebsiteOnlyAndAppOnly()
    {
        _sut.AddProfile(
            NewProfile(
                "appAndSite",
                ["chrome"],
                ["docs.google.com"],
                1
            )
        );
        _sut.AddProfile(
            NewProfile(
                "siteOnly",
                [],
                ["docs.google.com"],
                100
            )
        );
        _sut.AddProfile(
            NewProfile("appOnly", ["chrome"], [], 100)
        );

        var result = _sut.MatchProfile("chrome", "https://docs.google.com/document/d/abc");

        Assert.Equal(MatchKind.AppAndWebsite, result.Kind);
        Assert.Equal("appAndSite", result.Profile?.Name);
        Assert.Equal("docs.google.com", result.MatchedDomain);
    }

    [Fact]
    public void WebsiteBeatsAppWhenAppAndWebsiteAbsent()
    {
        _sut.AddProfile(
            NewProfile("siteOnly", [], ["docs.google.com"], 1)
        );
        _sut.AddProfile(
            NewProfile("appOnly", ["chrome"], [], 100)
        );

        var result = _sut.MatchProfile("chrome", "https://docs.google.com/document/d/abc");

        Assert.Equal(MatchKind.Website, result.Kind);
        Assert.Equal("siteOnly", result.Profile?.Name);
    }

    [Fact]
    public void AppBeatsGlobalWhenWebsiteTiersEmpty()
    {
        _sut.AddProfile(NewProfile("global", [], [], 100));
        _sut.AddProfile(
            NewProfile("appOnly", ["chrome"], [], 1)
        );

        var result = _sut.MatchProfile("chrome", null);

        Assert.Equal(MatchKind.App, result.Kind);
        Assert.Equal("appOnly", result.Profile?.Name);
    }

    [Fact]
    public void GlobalUsedWhenNothingElseMatches()
    {
        _sut.AddProfile(NewProfile("global", [], [], 0));

        var result = _sut.MatchProfile("notepad", null);

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.Equal("global", result.Profile?.Name);
    }

    [Fact]
    public void WonByPriorityFlagsUniqueWinnerOverLowerPriorityPeer()
    {
        _sut.AddProfile(NewProfile("low", ["chrome"], [], 1));
        _sut.AddProfile(
            NewProfile("high", ["chrome"], [], 50)
        );

        var result = _sut.MatchProfile("chrome", null);

        Assert.Equal("high", result.Profile?.Name);
        Assert.Equal(1, result.CompetingProfileCount);
        Assert.True(result.WonByPriority);
    }

    [Fact]
    public void CompetingProfileCountReflectsPriorityTie()
    {
        _sut.AddProfile(NewProfile("a", ["chrome"], [], 50));
        _sut.AddProfile(NewProfile("b", ["chrome"], [], 50));

        var result = _sut.MatchProfile("chrome", null);

        Assert.Equal(MatchKind.App, result.Kind);
        Assert.Equal(2, result.CompetingProfileCount);
        Assert.False(result.WonByPriority);
    }

    [Fact]
    public void NoMatchReturnedWhenNothingApplies()
    {
        _sut.AddProfile(
            NewProfile("appOnly", ["chrome"], [], 0)
        );

        var result = _sut.MatchProfile("notepad", null);

        Assert.Equal(MatchKind.NoMatch, result.Kind);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void ForcedProfileIdReturnsManualOverride()
    {
        var forced = NewProfile("forced", [], [], 0);
        _sut.AddProfile(forced);

        var result = _sut.MatchProfile("chrome", null, forced.Id);

        Assert.Equal(MatchKind.ManualOverride, result.Kind);
        Assert.Equal(forced.Id, result.Profile?.Id);
    }

    private static Profile NewProfile(
        string name,
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> urlPatterns,
        int priority
    )
    {
        return new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsEnabled = true,
            Priority = priority,
            ProcessNames = processNames,
            UrlPatterns = urlPatterns,
        };
    }
}