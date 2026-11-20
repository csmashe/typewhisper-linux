using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     The compositor-reported PID must reach the snapshot, or the URL and screen-context
///     readers can't tell two same-title browser instances apart.
/// </summary>
public sealed class CompositorActiveWindowProviderTests
{
    // Above Linux's pid_max, so no /proc entry exists and the parse stays deterministic.
    private const int ForeignPid = 2147483000;

    private const string KWinWindowId = "{6f1c2c7e-0000-4000-8000-000000000001}";

    [Fact]
    public async Task HyprlandSnapshotCarriesTheFocusedWindowsPid()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (file, _) => file == "hyprctl",
            $$"""{"address":"0x55","class":"firefox","title":"Same Title","pid":{{ForeignPid}}}"""
        );

        var snapshot = await new HyprlandActiveWindowProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("firefox", snapshot.ProcessName);
        Assert.Equal(ForeignPid, snapshot.ProcessId);
    }

    [Fact]
    public async Task HyprlandSnapshotHasNoPidWhenHyprctlReportsNone()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (file, _) => file == "hyprctl",
            """{"address":"0x55","class":"firefox","title":"Same Title","pid":-1}"""
        );

        var snapshot = await new HyprlandActiveWindowProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.ProcessId);
        // Other providers leave the old name-and-title match in place.
        Assert.False(snapshot.ProcessIdUnresolved);
    }

    [Fact]
    public async Task SwaySnapshotCarriesTheFocusedWindowsPid()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (file, _) => file == "swaymsg",
            $$"""{"id":1,"nodes":[{"id":7,"focused":true,"app_id":"firefox","name":"Same Title","pid":{{ForeignPid}}}]}"""
        );

        var snapshot = await new SwayActiveWindowProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("firefox", snapshot.ProcessName);
        Assert.Equal(ForeignPid, snapshot.ProcessId);
    }

    [Fact]
    public async Task GnomeWindowCallsSnapshotCarriesTheFocusedWindowsPid()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (file, _) => file == "gdbus",
            $$"""('[{"id":1,"title":"Same Title","wm_class":"firefox","pid":{{ForeignPid}},"has_focus":false},{"id":2,"title":"Same Title","wm_class":"firefox","pid":{{ForeignPid - 1}},"has_focus":true}]',)"""
        );

        var snapshot = await new GnomeWindowCallsProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("firefox", snapshot.ProcessName);
        Assert.Equal("2", snapshot.WindowId);
        Assert.Equal(ForeignPid - 1, snapshot.ProcessId);
    }

    [Fact]
    public async Task GnomeWindowCallsSnapshotHasNoPidWhenWindowCallsReportsNone()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (file, _) => file == "gdbus",
            """('[{"id":2,"title":"Same Title","wm_class":"firefox","pid":0,"has_focus":true}]',)"""
        );

        var snapshot = await new GnomeWindowCallsProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("firefox", snapshot.ProcessName);
        Assert.Null(snapshot.ProcessId);
        // Other providers leave the old name-and-title match in place.
        Assert.False(snapshot.ProcessIdUnresolved);
    }

    [Fact]
    public async Task GnomeShellIntrospectSnapshotCarriesTheFocusedWindowsPid()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (file, _) => file == "gdbus",
            $$$"""({uint64 1: {'app-id': <'firefox.desktop'>, 'wm-class': <'firefox'>, 'title': <'Same Title'>, 'has-focus': <false>, 'pid': <uint32 {{{ForeignPid}}}>}, uint64 2: {'app-id': <'firefox.desktop'>, 'wm-class': <'firefox'>, 'title': <'Same Title'>, 'has-focus': <true>, 'pid': <uint32 {{{ForeignPid - 1}}}>}},)"""
        );

        var snapshot = await new GnomeShellActiveWindowProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("firefox", snapshot.ProcessName);
        Assert.Equal("2", snapshot.WindowId);
        Assert.Equal(ForeignPid - 1, snapshot.ProcessId);
    }

    [Fact]
    public async Task KWinSnapshotCarriesTheFocusedWindowsPid()
    {
        var runner = KWinRunner($"{ForeignPid}\n");

        var snapshot = await new KWinActiveWindowProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("firefox", snapshot.ProcessName);
        Assert.Equal("Same Title", snapshot.Title);
        Assert.Equal(KWinWindowId, snapshot.WindowId);
        Assert.Equal(ForeignPid, snapshot.ProcessId);
        Assert.False(snapshot.ProcessIdUnresolved);
        // The PID comes from the captured window, not a second getactivewindow.
        Assert.Contains(
            runner.SupervisorInvocations,
            invocation => invocation.Command.Arguments.SequenceEqual(["getwindowpid", KWinWindowId])
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("0")]
    [InlineData("-42")]
    [InlineData("+42")]
    [InlineData("42 43")]
    [InlineData("42\n43")]
    [InlineData("not a pid")]
    [InlineData("99999999999")]
    public async Task KWinSnapshotHasNoPidWhenKdotoolPrintsNoUsablePid(string output)
    {
        var snapshot = await new KWinActiveWindowProvider(KWinRunner(output))
            .TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("firefox", snapshot.ProcessName);
        Assert.Null(snapshot.ProcessId);
        Assert.True(snapshot.ProcessIdUnresolved);
    }

    [Fact]
    public async Task KWinSnapshotHasNoPidWhenGetwindowpidFails()
    {
        var runner = new FakeProcessRunner();
        runner.FailWhen(IsGetWindowPid); // Overrides match in order, so this one wins.
        AddKWinWindow(runner, $"{ForeignPid}");

        var snapshot = await new KWinActiveWindowProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Same Title", snapshot.Title);
        Assert.Null(snapshot.ProcessId);
        Assert.True(snapshot.ProcessIdUnresolved);
    }

    [Fact]
    public async Task KWinSnapshotHasNoPidWhenGetwindowpidTimesOut()
    {
        var runner = new FakeProcessRunner();
        runner.TimeOutWhen(IsGetWindowPid);
        AddKWinWindow(runner, $"{ForeignPid}");

        var snapshot = await new KWinActiveWindowProvider(runner).TryGetActiveWindowAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Same Title", snapshot.Title);
        Assert.Null(snapshot.ProcessId);
        Assert.True(snapshot.ProcessIdUnresolved);
    }

    private static bool IsGetWindowPid(string file, IReadOnlyList<string> args)
    {
        return file == "kdotool" && args.Count > 0 && args[0] == "getwindowpid";
    }

    private static FakeProcessRunner KWinRunner(string pidOutput)
    {
        var runner = new FakeProcessRunner();
        AddKWinWindow(runner, pidOutput);
        return runner;
    }

    private static void AddKWinWindow(FakeProcessRunner runner, string pidOutput)
    {
        runner.RespondWith((file, args) => file == "kdotool" && args[0] == "getactivewindow", KWinWindowId + "\n");
        runner.RespondWith(
            (file, args) => file == "kdotool" && args.SequenceEqual(["getwindowclassname", KWinWindowId]),
            "firefox\n"
        );
        runner.RespondWith(
            (file, args) => file == "kdotool" && args.SequenceEqual(["getwindowname", KWinWindowId]),
            "Same Title\n"
        );
        runner.RespondWith(
            (file, args) => file == "kdotool" && args.SequenceEqual(["getwindowpid", KWinWindowId]),
            pidOutput
        );
    }
}
