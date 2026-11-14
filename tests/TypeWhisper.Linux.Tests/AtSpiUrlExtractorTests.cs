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
    public void TryGetBrowserUrl_Polling_ServesCachedUrlWithoutASecondWalk()
    {
        var walks = 0;
        var sut = Build(_ =>
        {
            walks++;
            return "https://example.com/";
        });

        Assert.Equal("https://example.com/", sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));
        Assert.Equal("https://example.com/", sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));

        Assert.Equal(1, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_Dictation_RewalksASameTitleTabInsteadOfReusingTheCache()
    {
        var current = "http://127.0.0.1:19794/tab-a";
        var walks = 0;
        // ReSharper disable once AccessToModifiedClosure -- the stub deliberately returns whichever tab is current when it walks.
        var sut = Build(_ =>
        {
            walks++;
            return current;
        });

        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, "Same title"));

        // Switching to another tab with the same title leaves the cache key unchanged.
        current = "http://localhost:19794/tab-b";
        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, "Same title"));
        Assert.Equal(2, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_DictationWalk_RefreshesTheCacheForPolling()
    {
        var current = "http://127.0.0.1:19794/tab-a";
        var walks = 0;
        // ReSharper disable once AccessToModifiedClosure -- the stub deliberately returns whichever tab is current when it walks.
        var sut = Build(_ =>
        {
            walks++;
            return current;
        });

        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, "Same title", isPolling: true));
        current = "http://localhost:19794/tab-b";
        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, "Same title"));

        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, "Same title", isPolling: true));
        Assert.Equal(2, walks);
    }

    [Fact]
    public void ForgetCachedUrl_RejectedDictationRead_IsNotReusedByPolling()
    {
        var current = "http://localhost:19794/tab-b";
        var walks = 0;
        // ReSharper disable once AccessToModifiedClosure -- the stub deliberately returns whichever tab is current when it walks.
        var sut = Build(_ =>
        {
            walks++;
            return current;
        });

        // The dictation walk ran after a switch to tab B but is keyed on tab A's title.
        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, "Tab A"));
        sut.ForgetCachedUrl(Browser, null, "Tab A", null, current);

        current = "http://127.0.0.1:19794/tab-a";
        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, "Tab A", isPolling: true));
        Assert.Equal(2, walks);
    }

    [Fact]
    public void ForgetCachedUrl_LeavesADifferentCachedUrlAlone()
    {
        var walks = 0;
        var sut = Build(_ =>
        {
            walks++;
            return "https://example.com/";
        });

        Assert.Equal("https://example.com/", sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));
        sut.ForgetCachedUrl(Browser, null, "Example", null, "https://other.example/");

        Assert.Equal("https://example.com/", sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));
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

        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));
        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));

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
        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));
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

        Assert.Null(sut.TryGetBrowserUrl(Browser, "First tab", isPolling: true));
        Assert.Null(sut.TryGetBrowserUrl(Browser, "Second tab", isPolling: true));

        Assert.Equal(2, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_SuccessAfterMiss_ClearsTheMissState()
    {
        var walks = 0;
        var sut = Build(_ => ++walks == 1 ? null : "https://example.com/");

        Assert.Null(sut.TryGetBrowserUrl(Browser, "Example", isPolling: true));
        Assert.Equal(
            "https://example.com/",
            sut.TryGetBrowserUrl(Browser, "Other", isPolling: true)
        );

        // Back on "Example": the success cleared the miss record, so this walks again
        // rather than being suppressed by the stale backoff.
        Assert.Equal(
            "https://example.com/",
            sut.TryGetBrowserUrl(Browser, "Example", isPolling: true)
        );
        Assert.Equal(3, walks);
    }

    [Fact]
    public void TryGetBrowserUrl_Polling_DoesNotServeAnotherInstancesCachedUrl()
    {
        var current = "http://127.0.0.1:19794/a.html";
        var walks = 0;
        // ReSharper disable once AccessToModifiedClosure -- the stub deliberately returns whichever instance is focused when it walks.
        var sut = Build(_ =>
        {
            walks++;
            return current;
        });

        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, null, "Same title", isPolling: true, focusedProcessId: 100));

        // A second instance with the same title must walk, not reuse the first instance's URL.
        current = "http://localhost:19794/b.html";
        Assert.Equal(current, sut.TryGetBrowserUrl(Browser, null, "Same title", isPolling: true, focusedProcessId: 200));
        Assert.Equal(2, walks);
    }

    [Theory]
    // Same process, or the focused PID as seen inside the app's PID namespace (Flatpak).
    [InlineData(100, "firefox-bin", 100, null, "firefox-bin", false)]
    [InlineData(2, "firefox-bin", 4242, 2, "firefox-bin", false)]
    // Another instance of the same browser, under the same or an aliased process name.
    [InlineData(200, "firefox-bin", 100, 100, "firefox-bin", true)]
    [InlineData(200, "firefox", 100, 100, "firefox-bin", true)]
    [InlineData(200, "code", 100, 100, "code", true)]
    // A process that can't be attributed to the focused program (e.g. Flatpak's a11y bus proxy).
    [InlineData(200, "firefox-bin", 100, 100, "xdg-dbus-proxy", false)]
    [InlineData(200, "firefox-bin", 100, 100, null, false)]
    [InlineData(200, null, 100, 100, "firefox-bin", false)]
    public void IsAnotherInstance_RejectsOnlyADifferentInstanceOfTheFocusedProgram(
        int focusedProcessId,
        string? focusedProcessName,
        int appProcessId,
        int? appInnermostProcessId,
        string? appProcessName,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            AtSpiUrlExtractor.IsAnotherInstance(
                focusedProcessId,
                focusedProcessName,
                appProcessId,
                appInnermostProcessId,
                appProcessName
            )
        );
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
