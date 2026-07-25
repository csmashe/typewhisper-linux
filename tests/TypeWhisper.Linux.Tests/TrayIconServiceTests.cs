using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers <see cref="TrayIconService.ProbeTrayAvailable" /> — the D-Bus probe
///     that decides whether close-to-tray is safe (backlog #18). The probe reads
///     the StatusNotifierWatcher's <c>IsStatusNotifierHostRegistered</c> property
///     (true only when a watcher exists *and* a host registered with it). Probe
///     logic is testable through the <see cref="IProcessRunner" /> seam.
/// </summary>
public sealed class TrayIconServiceTests
{
    [Fact]
    public void Language_change_updates_existing_native_menu_labels()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var runner = new FakeProcessRunner();
            runner.RespondWith((file, _) => file == "gdbus", "(<true>,)\n");
            using var sut = new TrayIconService(runner);
            sut.Initialize();
            var englishLabels = sut.MenuLabels.ToArray();

            Loc.Instance.CurrentLanguage = "de";

            Assert.True(sut.IsMenuBuilt);
            Assert.Equal(
                [
                    Loc.Instance["Tray.ToggleDictation"],
                    Loc.Instance["Tray.Settings"],
                    Loc.Instance["Tray.Exit"],
                ],
                sut.MenuLabels
            );
            Assert.NotEqual(englishLabels, sut.MenuLabels);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
        }
    }

    [Fact]
    public void Language_change_before_initialization_is_safe_and_does_not_build_menu()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var runner = new FakeProcessRunner();
            runner.RespondWith((file, _) => file == "gdbus", "(<true>,)\n");
            using var sut = new TrayIconService(runner);

            var exception = Record.Exception(() => Loc.Instance.CurrentLanguage = "de");

            Assert.Null(exception);
            Assert.False(sut.IsMenuBuilt);
            Assert.Empty(sut.MenuLabels);

            sut.Initialize();

            Assert.True(sut.IsMenuBuilt);
            Assert.Equal(3, sut.MenuLabels.Count);
            Assert.Equal(Loc.Instance["Tray.ToggleDictation"], sut.MenuLabels[0]);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
        }
    }

    [Fact]
    public void Tray_is_available_when_a_host_is_registered()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith((file, _) => file == "gdbus", "(<true>,)\n");
        using var sut = new TrayIconService(runner);

        Assert.True(sut.ProbeTrayAvailable());
    }

    [Fact]
    public void Tray_is_unavailable_when_a_watcher_exists_but_no_host_registered()
    {
        // A stale or hostless watcher: the name is owned, but no tray draws
        // icons. Name-ownership alone would mis-report this as available.
        var runner = new FakeProcessRunner();
        runner.RespondWith((file, _) => file == "gdbus", "(<false>,)\n");
        using var sut = new TrayIconService(runner);

        Assert.False(sut.ProbeTrayAvailable());
    }

    [Fact]
    public void Tray_is_unavailable_when_the_probe_cannot_run()
    {
        // No watcher at all (gdbus errors on the missing dest), gdbus
        // missing, or the session bus unreachable — fail safe to "no tray"
        // so close-to-tray falls back to quitting rather than stranding.
        var runner = new FakeProcessRunner { Default = FakeProcessRunner.NotStarted() };
        using var sut = new TrayIconService(runner);

        Assert.False(sut.ProbeTrayAvailable());
    }
}
