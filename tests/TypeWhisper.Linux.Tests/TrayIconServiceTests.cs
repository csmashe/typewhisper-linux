using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Covers <see cref="TrayIconService.ProbeTrayAvailable"/> — the D-Bus probe
/// that decides whether close-to-tray is safe (backlog #18). The probe reads
/// the StatusNotifierWatcher's <c>IsStatusNotifierHostRegistered</c> property
/// (true only when a watcher exists *and* a host registered with it). Probe
/// logic is testable through the <see cref="IProcessRunner"/> seam; the
/// Avalonia <c>TrayIcon</c> wiring in <c>Initialize()</c> is verified manually.
/// </summary>
public sealed class TrayIconServiceTests
{
    [Fact]
    public void Tray_is_available_when_a_host_is_registered()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith((file, _) => file == "gdbus", "(<true>,)\n");

        Assert.True(new TrayIconService(runner).ProbeTrayAvailable());
    }

    [Fact]
    public void Tray_is_unavailable_when_a_watcher_exists_but_no_host_registered()
    {
        // A stale or hostless watcher: the name is owned, but no tray draws
        // icons. Name-ownership alone would mis-report this as available.
        var runner = new FakeProcessRunner();
        runner.RespondWith((file, _) => file == "gdbus", "(<false>,)\n");

        Assert.False(new TrayIconService(runner).ProbeTrayAvailable());
    }

    [Fact]
    public void Tray_is_unavailable_when_the_probe_cannot_run()
    {
        // No watcher at all (gdbus errors on the missing dest), gdbus
        // missing, or the session bus unreachable — fail safe to "no tray"
        // so close-to-tray falls back to quitting rather than stranding.
        var runner = new FakeProcessRunner { Default = FakeProcessRunner.NotStarted() };

        Assert.False(new TrayIconService(runner).ProbeTrayAvailable());
    }
}
