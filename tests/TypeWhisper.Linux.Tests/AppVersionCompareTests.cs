using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Pure-function tests for the SemVer-ish comparison the update checker uses
///     to decide whether the latest GitHub release is newer than the running
///     build. No network or disk.
/// </summary>
public sealed class AppVersionCompareTests
{
    [Theory]
    [InlineData("0.5.0", "0.6.0")] // patch/minor bump is newer
    [InlineData("0.5.0", "0.5.1")]
    [InlineData("0.9.0", "0.10.0")] // double-digit minor sorts above single-digit
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("0.5.0-rc.1", "0.5.0")] // release outranks its own pre-release
    [InlineData("0.5.0-rc.1", "0.5.0-rc.2")]
    [InlineData("0.5.0-rc.2", "0.5.0-rc.10")] // numeric prerelease ids sort numerically, not lexically
    [InlineData("0.5.0-rc.1", "0.5.0-rc.1.1")] // a longer prerelease outranks its prefix
    [InlineData("0.5.0-alpha", "0.5.0-beta")] // alphanumeric ids sort in ASCII order
    [InlineData("0.5.0-1", "0.5.0-alpha")] // numeric id ranks below alphanumeric
    public void Compare_ReturnsNegative_WhenFirstIsOlder(string older, string newer)
    {
        Assert.True(AppVersion.Compare(older, newer) < 0);
        Assert.True(AppVersion.Compare(newer, older) > 0);
    }

    [Theory]
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("v0.5.0", "0.5.0")] // leading v is ignored
    [InlineData("0.5.0+abc123", "0.5.0")] // build metadata is ignored
    [InlineData("0.5", "0.5.0")] // missing patch component normalizes to 0
    public void Compare_ReturnsZero_WhenEquivalent(string a, string b)
    {
        Assert.Equal(0, AppVersion.Compare(a, b));
    }

    [Fact]
    public void Compare_TreatsNullOrEmpty_AsZeroVersion()
    {
        Assert.True(AppVersion.Compare(null, "0.1.0") < 0);
        Assert.True(AppVersion.Compare("0.1.0", "") > 0);
        Assert.Equal(0, AppVersion.Compare(null, ""));
    }

    [Theory]
    [InlineData("0.13.0")]
    [InlineData("0.13.0-rc.2")]
    [InlineData("0.13.0-rc.2+sha.abc-123")]
    [InlineData("1.0.0-alpha.beta-1")]
    [InlineData("999999999999999999999999.0.1")]
    public void TryParseStrict_AcceptsValidSemVer(string version)
    {
        Assert.True(AppVersion.TryParseStrict(version, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0.13")]
    [InlineData("0.13.0.1")]
    [InlineData("v0.13.0")]
    [InlineData("01.13.0")]
    [InlineData("0.13.0-rc.02")]
    [InlineData("0.13.0-rc..2")]
    [InlineData("0.13.0-rc_2")]
    [InlineData("0.13.0-")]
    [InlineData("0.13.0+")]
    [InlineData("0.13.0+build..sha")]
    [InlineData(" 0.13.0")]
    public void TryParseStrict_RejectsMalformedSemVer(string? version)
    {
        Assert.False(AppVersion.TryParseStrict(version, out _));
    }

    [Theory]
    [InlineData("0.13.0-rc.2", "0.13.0")]
    [InlineData("0.13.0-rc.2", "0.13.0-rc.10")]
    public void TryCompareStrict_UsesSemVerPrereleasePrecedence(string older, string newer)
    {
        Assert.True(AppVersion.TryCompareStrict(older, newer, out var comparison));
        Assert.True(comparison < 0);

        Assert.True(AppVersion.TryCompareStrict(newer, older, out comparison));
        Assert.True(comparison > 0);
    }

    [Fact]
    public void TryCompareStrict_IgnoresBuildMetadata()
    {
        Assert.True(
            AppVersion.TryCompareStrict(
                "0.13.0-rc.2+sha.abc",
                "0.13.0-rc.2+sha.def",
                out var comparison
            )
        );
        Assert.Equal(0, comparison);
    }

    [Fact]
    public void TryCompareStrict_RejectsMalformedInput()
    {
        Assert.False(AppVersion.TryCompareStrict("0.13", "0.13.0", out _));
        Assert.False(AppVersion.TryCompareStrict("0.13.0", "not-semver", out _));
    }
}
