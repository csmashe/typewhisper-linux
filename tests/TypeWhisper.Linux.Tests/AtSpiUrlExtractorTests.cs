using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Exercises <see cref="AtSpiUrlExtractor" />'s cache and miss-backoff state machine through
///     the real extractor, with the AT-SPI walk replaced by a counting stub.
/// </summary>
public sealed class AtSpiUrlExtractorTests
{
    private const string Browser = "firefox";

    [Fact]
    public void TryGetBrowserUrl_UnsupportedProcess_NeverWalks()
    {
        var walks = 0;
        var sut = Build(_ =>
        {
            walks++;
            return "https://example.com/";
        });

        Assert.Null(sut.TryGetBrowserUrl("kate", "Untitled"));
        Assert.Equal(0, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_CachedUrl_ServesWithoutASecondWalk()
    {
        var walks = 0;
        var sut = Build(_ =>
        {
            walks++;
            return "https://example.com/";
        });

        Assert.Equal("https://example.com/", sut.TryGetBrowserUrl(Browser, "Example"));
        Assert.Equal("https://example.com/", sut.TryGetBrowserUrl(Browser, "Example"));

        Assert.Equal(1, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_TitleChange_RewalksBecauseTheTabMayHaveChanged()
    {
        var walks = 0;
        var sut = Build(_ => $"https://example.com/{walks++}");

        Assert.Equal("https://example.com/0", sut.TryGetBrowserUrl(Browser, "First tab"));
        Assert.Equal("https://example.com/1", sut.TryGetBrowserUrl(Browser, "Second tab"));

        Assert.Equal(2, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_MissBackoff_SuppressesRepeatWalkForSameProcessAndTitle()
    {
        var walks = 0;
        var sut = Build(_ =>
        {
            walks++;
            return null;
        });

        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", honorMissBackoff: true));
        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", honorMissBackoff: true));

        Assert.Equal(1, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_MissBackoff_DoesNotSuppressWhenNotHonored()
    {
        var walks = 0;
        var sut = Build(_ =>
        {
            walks++;
            return null;
        });

        // Dictation passes false so a poll's miss can never suppress its own walk.
        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", honorMissBackoff: true));
        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example"));

        Assert.Equal(2, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_MissBackoff_IsBypassedByATitleChange()
    {
        var walks = 0;
        var sut = Build(_ =>
        {
            walks++;
            return null;
        });

        Assert.Null(sut.TryGetBrowserUrl(Browser, "First tab", honorMissBackoff: true));
        Assert.Null(sut.TryGetBrowserUrl(Browser, "Second tab", honorMissBackoff: true));

        Assert.Equal(2, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_SuccessAfterMiss_ClearsTheMissState()
    {
        var walks = 0;
        var sut = Build(_ => ++walks == 1 ? null : "https://example.com/");

        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", honorMissBackoff: true));
        Assert.Equal(
            "https://example.com/",
            sut.TryGetBrowserUrl(Browser, "Other", honorMissBackoff: true)
        );

        // Back on "Example": the success cleared the miss record, so this walks again
        // rather than being suppressed by the stale backoff.
        Assert.Equal(
            "https://example.com/",
            sut.TryGetBrowserUrl(Browser, "Example", honorMissBackoff: true)
        );
        Assert.Equal(3, walks);
    }

    private static AtSpiUrlExtractor Build(Func<string, string?> walk)
    {
        // Fake runner so the constructor's busctl/gdbus availability probes stay off the host.
        return new AtSpiUrlExtractor(
            new FakeProcessRunner(),
            errorLog: null,
            walkOverride: walk
        );
    }
}
