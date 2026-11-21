using System.Net;
using System.Text;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class UpdateCheckServiceTests : IDisposable
{
    private const string NewVersion = "9999.0.0";

    private readonly string _directory = TestPaths.CreateTempDirectory("update-check");
    private readonly ReleasesHandler _handler = new();
    private readonly LinuxPreferencesService _prefs;
    private readonly List<TimeSpan> _delays = [];
    private DateTime _now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private int _maxDelays = 2;
    private Action<int>? _afterDelay;

    public UpdateCheckServiceTests()
    {
        _prefs = new LinuxPreferencesService(Path.Join(_directory, "linux-preferences.json"));
    }

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_directory);
    }

    [Fact]
    public async Task Loop_ChecksAtStartupAndAgainADayLaterWhileRunning()
    {
        var sut = CreateService();

        await RunLoopAsync(sut);

        Assert.Equal(2, _handler.Requests);
        Assert.Equal(TimeSpan.FromHours(24), _delays[0]);
        Assert.True(sut.LastResult.UpdateAvailable);
        Assert.Equal(NewVersion, sut.LastResult.LatestVersion);
    }

    [Fact]
    public async Task Loop_WaitsWhileRecordingAndChecksOnceRecordingEnds()
    {
        var recording = true;
        _afterDelay = _ => recording = false;
        var sut = CreateService();

        await RunLoopAsync(sut, () => recording);

        Assert.Equal(TimeSpan.FromMinutes(15), _delays[0]);
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task Loop_RetriesAFailedCheckAfterAnHour()
    {
        _handler.Fail = true;
        _maxDelays = 3;
        var sut = CreateService();

        await RunLoopAsync(sut);

        Assert.Equal(TimeSpan.FromHours(1), _delays[0]);
        Assert.Equal(TimeSpan.FromHours(1), _delays[1]);
        Assert.Equal(3, _handler.Requests);
        Assert.Null(_prefs.Current.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task Loop_StopsWhenTheOptOutIsSet()
    {
        _prefs.Update(p => p with { CheckForUpdatesOnStartup = false });
        var sut = CreateService();

        await sut.RunBackgroundChecksAsync(() => false);

        Assert.Equal(0, _handler.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Loop_UsesTheCachedResultUntilTheDailyCheckIsDue()
    {
        _prefs.Update(p => p with
        {
            LastUpdateCheckUtc = _now - TimeSpan.FromHours(20),
            LastKnownLatestVersion = NewVersion,
            LastKnownLatestUrl = "https://example.com/cached",
        });
        _maxDelays = 1;
        var sut = CreateService();

        await RunLoopAsync(sut);

        Assert.Equal(0, _handler.Requests);
        Assert.Equal(TimeSpan.FromHours(4), _delays[0]);
        Assert.Equal("https://example.com/cached", sut.LastResult.ReleaseUrl);
    }

    [Fact]
    public void Later_HidesTheBannerForADayOnly()
    {
        var sut = CreateService();
        var raised = 0;
        sut.ResultChanged += _ => raised++;

        sut.SnoozeUpdate();

        Assert.Equal(1, raised);
        Assert.True(sut.IsSuppressed(NewVersion));
        _now += UpdateCheckService.s_snoozeDuration;
        Assert.False(sut.IsSuppressed(NewVersion));
    }

    [Fact]
    public async Task Loop_WakesWhenLaterEndsSoTheBannerReturns()
    {
        _maxDelays = 1;
        var sut = CreateService();
        _prefs.Update(p => p with { UpdateSnoozedUntilUtc = _now + TimeSpan.FromHours(3) });

        await RunLoopAsync(sut);

        Assert.Equal(TimeSpan.FromHours(3), _delays[0]);
    }

    [Fact]
    public async Task Loop_ShowsTheCachedUpdateWhenLaterEndsEvenIfThatRefreshFails()
    {
        _prefs.Update(p => p with
        {
            LastUpdateCheckUtc = _now - TimeSpan.FromHours(23.5),
            LastKnownLatestVersion = NewVersion,
            UpdateSnoozedUntilUtc = _now + TimeSpan.FromMinutes(30),
        });
        _handler.Fail = true;
        var sut = CreateService();
        var published = new List<UpdateCheckResult>();
        sut.ResultChanged += published.Add;

        await RunLoopAsync(sut);

        Assert.Equal(TimeSpan.FromMinutes(30), _delays[0]);
        Assert.Equal(1, _handler.Requests);
        var last = published[^1];
        Assert.True(last is { Faulted: false, UpdateAvailable: true });
        Assert.False(sut.IsSuppressed(last.LatestVersion));
    }

    [Fact]
    public async Task Loop_TreatsACheckStampFromTheFutureAsDue()
    {
        _prefs.Update(p => p with { LastUpdateCheckUtc = _now + TimeSpan.FromDays(30) });
        _maxDelays = 1;
        var sut = CreateService();

        await RunLoopAsync(sut);

        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public void Skip_HidesOnlyThatVersion()
    {
        var sut = CreateService();

        sut.DismissUpdate(NewVersion);

        Assert.True(sut.IsSuppressed(NewVersion));
        Assert.False(sut.IsSuppressed("10000.0.0"));
    }

    [Fact]
    public async Task Loop_AnnouncesEachNewVersionOnce()
    {
        _maxDelays = 3;
        var sut = CreateService();
        var notices = new List<string?>();
        sut.UpdateNoticeDue += result => notices.Add(result.LatestVersion);

        await RunLoopAsync(sut);

        Assert.Equal(3, _handler.Requests);
        Assert.Equal([NewVersion], notices);
        Assert.Equal(NewVersion, _prefs.Current.LastNotifiedUpdateVersion);

        // A restart must not announce the same version again.
        _delays.Clear();
        _maxDelays = 1;
        var restarted = CreateService();
        restarted.UpdateNoticeDue += result => notices.Add(result.LatestVersion);
        _now += TimeSpan.FromDays(1);
        await RunLoopAsync(restarted);
        Assert.Single(notices);
    }

    [Fact]
    public async Task Loop_DoesNotAnnounceASkippedOrSnoozedVersion()
    {
        _maxDelays = 1;
        var skipped = CreateService();
        skipped.DismissUpdate(NewVersion);
        var notices = 0;
        skipped.UpdateNoticeDue += _ => notices++;
        await RunLoopAsync(skipped);

        _prefs.Update(p => p with { DismissedUpdateVersion = null, UpdateSnoozedUntilUtc = _now + TimeSpan.FromDays(2) });
        _delays.Clear();
        _now += TimeSpan.FromDays(1);
        var snoozed = CreateService();
        snoozed.UpdateNoticeDue += _ => notices++;
        await RunLoopAsync(snoozed);

        Assert.Equal(0, notices);
        Assert.Null(_prefs.Current.LastNotifiedUpdateVersion);
    }

    private UpdateCheckService CreateService()
    {
        return new UpdateCheckService(_prefs, new HttpClient(_handler, disposeHandler: false), () => _now, Delay);
    }

    private static async Task RunLoopAsync(UpdateCheckService sut, Func<bool>? isRecording = null)
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.RunBackgroundChecksAsync(isRecording ?? (() => false))
        );
    }

    // Moves the clock instead of sleeping; the last allowed delay ends the loop.
    private Task Delay(TimeSpan span, CancellationToken cancellationToken)
    {
        _delays.Add(span);
        if (_delays.Count >= _maxDelays)
        {
            throw new OperationCanceledException();
        }

        _now += span;
        _afterDelay?.Invoke(_delays.Count);
        return Task.CompletedTask;
    }

    private sealed class ReleasesHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            const string body =
                $$"""[{"tag_name":"v{{NewVersion}}","html_url":"https://example.com/release","prerelease":false,"draft":false}]""";
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}

public sealed class UpdateNotificationServiceTests
{
    [Fact]
    public async Task Notify_SendsADesktopNotificationNamingTheVersion()
    {
        var runner = new FakeProcessRunner();

        await new UpdateNotificationService(runner).NotifyAsync("1.2.3");

        var call = Assert.Single(runner.Invocations);
        Assert.Equal("gdbus", call.FileName);
        Assert.Contains("org.freedesktop.Notifications.Notify", call.Args);
        Assert.Contains(Loc.Instance.GetString("Update.NotificationSummary", "1.2.3"), call.Args);
        Assert.Contains(Loc.Instance["Update.NotificationBody"], call.Args);
    }
}
