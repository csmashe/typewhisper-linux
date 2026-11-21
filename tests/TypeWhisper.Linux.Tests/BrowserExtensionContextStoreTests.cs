using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class BrowserExtensionContextStoreTests
{
    private static readonly ActiveWindowSnapshot s_firefox =
        new("firefox", "Docs — Mozilla Firefox", "0x1", "org.mozilla.firefox", "xdotool", 4242);

    [Fact]
    public void FocusedReportBelongsToTheWindowInFront()
    {
        var store = new BrowserExtensionContextStore(new ManualTimeProvider());

        var report = store.Report("client-a", true, "https://docs.example.com/a", "Docs", s_firefox);

        Assert.NotNull(report);
        Assert.Equal("firefox", report.ProcessName);
        Assert.Same(report, store.Current(s_firefox));
        Assert.Null(store.Current(s_firefox with { ProcessName = "chromium" }));
        Assert.Null(store.Current(s_firefox with { ProcessId = 7 }));
        Assert.Null(store.Current(s_firefox with { WindowId = "0x2" }));
        Assert.Null(store.Current(s_firefox with { ProcessIdUnresolved = true }));
        Assert.Null(store.Current(null));
    }

    [Fact]
    public void MissingPidOrWindowIdFallsBackToTheProcessName()
    {
        var store = new BrowserExtensionContextStore(new ManualTimeProvider());
        var noIds = s_firefox with { ProcessId = null, WindowId = null };

        store.Report("client-a", true, "https://example.com/", null, noIds);

        Assert.NotNull(store.Current(s_firefox));
        Assert.NotNull(store.Current(noIds with { ProcessName = "FIREFOX" }));
    }

    [Fact]
    public void ReportsExpireAfterThreeMinutes()
    {
        var time = new ManualTimeProvider();
        var store = new BrowserExtensionContextStore(time);
        store.Report("client-a", true, "https://example.com/", null, s_firefox);

        time.Advance(BrowserExtensionContextStore.ReportLifetime);
        Assert.NotNull(store.Current(s_firefox));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(store.Current(s_firefox));
    }

    [Fact]
    public void UnfocusedReportClearsOnlyItsOwnClient()
    {
        var store = new BrowserExtensionContextStore(new ManualTimeProvider());
        store.Report("client-a", true, "https://example.com/", null, s_firefox);

        Assert.Null(store.Report("client-b", false, null, null, null));
        Assert.NotNull(store.Current(s_firefox));

        Assert.Null(store.Report("client-a", false, null, null, null));
        Assert.Null(store.Current(s_firefox));
    }

    [Fact]
    public void ReportThatArrivedBeforeAnAppliedOneIsDropped()
    {
        var store = new BrowserExtensionContextStore(new ManualTimeProvider());
        var chromium = s_firefox with { ProcessName = "chromium", ProcessId = 5 };
        var older = store.BeginReport();
        var newer = store.BeginReport();

        Assert.NotNull(store.Report(newer, "client-b", true, "https://b.example/", null, chromium));
        Assert.Null(store.Report(older, "client-a", true, "https://a.example/", null, s_firefox));
        Assert.NotNull(store.Current(chromium));

        // Another client's clear doesn't block a pending report; its own clear does.
        var pending = store.BeginReport();
        store.Report("client-b", false, null, null, null);
        Assert.NotNull(store.Report(pending, "client-a", true, "https://a.example/", null, s_firefox));
        var stale = store.BeginReport();
        store.Report("client-a", false, null, null, null);
        Assert.Null(store.Report(stale, "client-a", true, "https://a.example/", null, s_firefox));
        Assert.Null(store.Current(s_firefox));
    }

    [Fact]
    public void ReportIsIgnoredWhileTypeWhisperOrAnUnknownOwnerIsInFront()
    {
        var store = new BrowserExtensionContextStore(new ManualTimeProvider());

        Assert.Null(store.Report("client-a", true, "https://example.com/", null, s_firefox with { ProcessId = Environment.ProcessId }));
        Assert.Null(store.Report("client-a", true, "https://example.com/", null, s_firefox with { ProcessIdUnresolved = true }));
        Assert.Null(store.Report("client-a", true, "https://example.com/", null, s_firefox with { ProcessName = "" }));
        Assert.Null(store.Report("client-a", true, "https://example.com/", null, null));
        Assert.Null(store.Current(s_firefox));
    }

    [Fact]
    public void ReportWithoutUrlMeansNoWebsite()
    {
        var store = new BrowserExtensionContextStore(new ManualTimeProvider());

        var report = store.Report("client-a", true, null, null, s_firefox);

        Assert.NotNull(report);
        Assert.Null(store.Current(s_firefox)!.Url);
    }

    [Theory]
    [InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("  http://example.com/  ", "http://example.com/")]
    [InlineData("file:///home/user/page.html", "file:///home/user/page.html")]
    [InlineData("chrome://settings", null)]
    [InlineData("about:newtab", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://exa mple.com/", null)]
    [InlineData("https://example.com/\a", null)]
    [InlineData("a:b", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeUrlKeepsOnlyWebAndFileUrls(string? value, string? expected)
    {
        Assert.Equal(expected, BrowserExtensionContextStore.NormalizeUrl(value));
    }

    [Fact]
    public void NormalizeUrlRejectsUrlsOf2048CharactersOrMore()
    {
        const string prefix = "https://example.com/";
        Assert.NotNull(BrowserExtensionContextStore.NormalizeUrl(prefix + new string('a', 2047 - prefix.Length)));
        Assert.Null(BrowserExtensionContextStore.NormalizeUrl(prefix + new string('a', 2048 - prefix.Length)));
    }

    [Fact]
    public void NormalizeTitleTrimsAndCapsWithoutSplittingASurrogatePair()
    {
        Assert.Null(BrowserExtensionContextStore.NormalizeTitle("   "));
        Assert.Equal("Docs", BrowserExtensionContextStore.NormalizeTitle("  Docs  "));
        Assert.Equal(512, BrowserExtensionContextStore.NormalizeTitle(new string('a', 600))!.Length);

        var split = new string('a', 511) + "😀" + "tail";
        Assert.Equal(new string('a', 511), BrowserExtensionContextStore.NormalizeTitle(split));
    }

    [Fact]
    public void ActiveWindowServicePrefersAReportOverTheAddressBarRead()
    {
        var runner = new FakeProcessRunner();
        var store = new BrowserExtensionContextStore(new ManualTimeProvider());
        var service = new ActiveWindowService([], new AtSpiUrlExtractor(runner), runner, store);
        var unknownBrowser = new ActiveWindowSnapshot("vivaldi-bin", "Inbox", "0x9", null, "xdotool", 99);

        Assert.Null(service.GetBrowserExtensionReport(unknownBrowser));
        store.Report("client-a", true, "https://mail.example.com/inbox", "Inbox", unknownBrowser);
        runner.SupervisorInvocations.Clear();

        Assert.NotNull(service.GetBrowserExtensionReport(unknownBrowser));
        Assert.Equal("https://mail.example.com/inbox", service.GetBrowserUrlForSnapshot(unknownBrowser));
        Assert.Empty(runner.SupervisorInvocations);

        store.Report("client-a", true, null, null, unknownBrowser);
        Assert.Null(service.GetBrowserUrlForSnapshot(unknownBrowser with { Title = "Example Domain - https://example.com" }));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }
}
