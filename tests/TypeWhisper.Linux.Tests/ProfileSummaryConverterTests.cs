using System.Globalization;
using Avalonia.Data.Converters;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Converters;
using TypeWhisper.Linux.Services.Localization;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProfileSummaryConverterTests
{
    [Fact]
    public void Convert_WithProfile_ReturnsFormattedSummary()
    {
        var profile = new Profile
        {
            Id = "p1",
            Name = "Auto Format",
            ProcessNames = ["code", "cursor"],
            UrlPatterns = ["github.com"],
        };

        var result = ProfileSummaryConverter.Instance.Convert(
            profile,
            typeof(string),
            null,
            CultureInfo.InvariantCulture
        );

        Assert.Equal("2 app rule(s), 1 URL rule(s)", result);
    }

    [Fact]
    public void Convert_WithZeroRules_ReturnsZeroCounts()
    {
        var profile = new Profile
        {
            Id = "p2",
            Name = "Empty Profile",
            ProcessNames = [],
            UrlPatterns = [],
        };

        var result = ProfileSummaryConverter.Instance.Convert(
            profile,
            typeof(string),
            null,
            CultureInfo.InvariantCulture
        );

        Assert.Equal("0 app rule(s), 0 URL rule(s)", result);
    }

    [Fact]
    public void Convert_MultiValue_ReturnsFormattedSummary()
    {
        var profile = new Profile
        {
            Id = "p1",
            Name = "Auto Format",
            ProcessNames = ["code", "cursor"],
            UrlPatterns = ["github.com"],
        };

        var result = ((IMultiValueConverter)ProfileSummaryConverter.Instance).Convert(
            [profile, "en"],
            typeof(string),
            null,
            CultureInfo.InvariantCulture
        );

        Assert.Equal("2 app rule(s), 1 URL rule(s)", result);
    }

    [Theory]
    [InlineData("en", "0 app rule(s), 0 URL rule(s)")]
    [InlineData("de", "0 App-Regel(n), 0 URL-Regel(n)")]
    [InlineData("es", "0 regla(s) de app, 0 regla(s) de URL")]
    [InlineData("ru", "Правил приложений: 0, правил URL: 0")]
    public void Convert_AcrossAllLanguages_UsesLocalizedSummary(string lang, string expected)
    {
        var originalLang = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = lang;
            var profile = new Profile
            {
                Id = "test",
                Name = "Test",
                ProcessNames = [],
                UrlPatterns = [],
            };

            var result = ProfileSummaryConverter.Instance.Convert(
                profile,
                typeof(string),
                null,
                CultureInfo.InvariantCulture
            );

            Assert.Equal(expected, result);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLang;
        }
    }

    [Fact]
    public void Convert_NonProfile_ReturnsEmptyString()
    {
        var result = ProfileSummaryConverter.Instance.Convert(
            "not a profile",
            typeof(string),
            null,
            CultureInfo.InvariantCulture
        );

        Assert.Equal(string.Empty, result);
    }
}
