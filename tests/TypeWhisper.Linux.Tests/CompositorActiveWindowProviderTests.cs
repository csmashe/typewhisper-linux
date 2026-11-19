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
}
