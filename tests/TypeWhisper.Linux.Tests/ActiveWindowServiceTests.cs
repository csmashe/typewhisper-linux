using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.PluginSDK.Processes;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ActiveWindowServiceTests
{
    [Fact]
    public void GetActiveWindowId_uses_discrete_bounded_probe()
    {
        var runner = new FakeProcessRunner
        {
            Default = new ProcessRunResult(
                true,
                false,
                0,
                "123\n",
                string.Empty
            ),
        };
        var extractor = new AtSpiUrlExtractor(runner);
        runner.SupervisorInvocations.Clear();
        var service = new ActiveWindowService([], extractor, runner);
        runner.SupervisorInvocations.Clear();

        Assert.Equal("123", service.GetActiveWindowId());

        var invocation = Assert.Single(runner.SupervisorInvocations);
        Assert.Equal("xdotool", invocation.Command.FileName);
        Assert.Equal(["getactivewindow"], invocation.Command.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(1), invocation.Options.Timeout);
        Assert.Equal(
            ProcessCaptureMode.Discard,
            invocation.Options.StandardError
        );
    }

    [Fact]
    public void Clipboard_write_abandons_pipes_the_xclip_daemon_keeps_open()
    {
        var runner = new FakeProcessRunner();
        var extractor = new AtSpiUrlExtractor(runner);
        var service = new ActiveWindowService(
            [new StubActiveWindowProvider("firefox", "123")],
            extractor,
            runner
        );
        runner.SupervisorInvocations.Clear();

        service.GetBrowserUrl();

        // Both the clear and the restore write go through xclip.
        var writes = runner.SupervisorInvocations
            .Where(invocation =>
                invocation.Command.FileName == "xclip"
                && invocation.Command.Arguments.SequenceEqual(
                    ["-selection", "clipboard"]
                )
            )
            .ToList();

        Assert.NotEmpty(writes);
        Assert.All(
            writes,
            write => Assert.Equal(
                ProcessPostExitPipePolicy.AbandonAfterGrace,
                write.Options.PostExitPipePolicy
            )
        );
    }

    private sealed class StubActiveWindowProvider(string processName, string windowId)
        : IActiveWindowProvider
    {
        public string Name => "xdotool";

        public bool IsApplicable()
        {
            return true;
        }

        public Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
        {
            return Task.FromResult<ActiveWindowSnapshot?>(
                new ActiveWindowSnapshot(
                    processName,
                    "Example — Mozilla Firefox",
                    windowId,
                    null,
                    "xdotool"
                )
            );
        }
    }

    [Theory]
    [InlineData("firefox", true)]
    [InlineData("Google Chrome", true)]
    [InlineData("Microsoft Edge", true)]
    [InlineData("org.mozilla.firefox", true)]
    [InlineData("Zen Browser", true)]
    [InlineData("zen", true)]
    [InlineData("zen-browser", true)]
    [InlineData("code", false)]
    [InlineData(null, false)]
    public void IsSupportedBrowserIdentity_RecognizesBrowserNames(string? identity, bool expected)
    {
        Assert.Equal(expected, ActiveWindowService.IsSupportedBrowserIdentity(identity));
    }

    [Theory]
    [InlineData("zen")]
    [InlineData("zen-browser")]
    [InlineData("zen-bin")]
    public void IsSupportedBrowserProcess_RecognizesZenBrowserProcesses(string processName)
    {
        Assert.True(ActiveWindowService.IsSupportedBrowserProcess(processName));
    }

    [Fact]
    public void IsSupportedBrowserWindow_RecognizesZenBrowserTitleWhenProcessUnknown()
    {
        Assert.True(
            ActiveWindowService.IsSupportedBrowserWindow(
                null,
                "Hey Ryan - chris@example.com - Mail — Zen Browser"
            )
        );
    }

    [Fact]
    public void TryInferBrowserProcessNameFromTitle_ReturnsZenForZenBrowser()
    {
        Assert.Equal(
            "zen",
            ActiveWindowService.TryInferBrowserProcessNameFromTitle("Inbox - Mail — Zen Browser")
        );
    }

    [Fact]
    public void TryInferBrowserUrlFromTitle_ReturnsGmailForZenMailWindow()
    {
        Assert.Equal(
            "https://mail.google.com",
            ActiveWindowService.TryInferBrowserUrlFromTitle(
                "Hey Ryan - chris@example.com - Excel On The Web Mail — Zen Browser"
            )
        );
    }

    [Fact]
    public void HasState_ReadsBitsAcrossWords()
    {
        var states = new uint[] { 0, 1u << 3 };

        Assert.True(ActiveWindowService.HasState(states, 35));
        Assert.False(ActiveWindowService.HasState(states, 11));
    }

    [Theory]
    [InlineData("https://example.com/path", "https://example.com/path")]
    [InlineData("example.com/path", "https://example.com/path")]
    [InlineData("not a url", null)]
    public void SanitizeCapturedBrowserUrl_NormalizesLikelyUrls(string value, string? expected)
    {
        Assert.Equal(expected, ActiveWindowService.SanitizeCapturedBrowserUrl(value));
    }

    [Fact]
    public void ScoreBrowserUrlCandidate_PrefersFocusedEditBarOverGenericEntry()
    {
        var focusedEditBarScore = ActiveWindowService.ScoreBrowserUrlCandidate(
            77,
            [1u << 11, 1u << 18],
            "Address and search bar",
            "https://example.com/path",
            ["org.a11y.atspi.Text"]
        );

        var entryScore = ActiveWindowService.ScoreBrowserUrlCandidate(
            79,
            [1u << 18],
            "Search",
            "example.com",
            ["org.a11y.atspi.Text"]
        );

        Assert.True(focusedEditBarScore > entryScore);
    }
}
