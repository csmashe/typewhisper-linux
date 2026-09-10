using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers <see cref="AccessibilityBusActivationService" />: parsing the busctl
///     get-property output, issuing the correct set-property calls, and failure handling.
/// </summary>
public sealed class AccessibilityBusActivationServiceTests
{
    private static bool IsGetIsEnabled(string file, IReadOnlyList<string> args) =>
        file == "busctl" && args.Contains("get-property") && args.Contains("IsEnabled");

    [Fact]
    public async Task IsActivatedAsync_parses_true()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(IsGetIsEnabled, "b true\n");
        var service = new AccessibilityBusActivationService(runner);

        Assert.Equal(true, await service.IsActivatedAsync());
    }

    [Fact]
    public async Task IsActivatedAsync_parses_false()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(IsGetIsEnabled, "b false\n");
        var service = new AccessibilityBusActivationService(runner);

        Assert.Equal(false, await service.IsActivatedAsync());
    }

    [Fact]
    public async Task IsActivatedAsync_returns_null_when_command_fails()
    {
        var runner = new FakeProcessRunner();
        runner.FailWhen(IsGetIsEnabled, "bus unreachable");
        var service = new AccessibilityBusActivationService(runner);

        Assert.Null(await service.IsActivatedAsync());
    }

    [Fact]
    public async Task IsActivatedAsync_returns_null_on_unparsable_output()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(IsGetIsEnabled, "unexpected");
        var service = new AccessibilityBusActivationService(runner);

        Assert.Null(await service.IsActivatedAsync());
    }

    [Fact]
    public async Task SetActivatedAsync_true_sets_only_IsEnabled()
    {
        var runner = new FakeProcessRunner();
        var service = new AccessibilityBusActivationService(runner);

        var ok = await service.SetActivatedAsync(true);

        Assert.True(ok);
        var setCall = Assert.Single(
            runner.Invocations,
            i => i.FileName == "busctl" && i.Args.Contains("set-property")
        );
        Assert.Contains("IsEnabled", setCall.Args);
        Assert.Equal("b", setCall.Args[^2]);
        Assert.Equal("true", setCall.Args[^1]);
    }

    [Fact]
    public async Task SetActivatedAsync_never_touches_ScreenReaderEnabled()
    {
        // GNOME mirrors ScreenReaderEnabled into the screen-reader-enabled gsettings key,
        // which LAUNCHES Orca and makes the whole desktop speak. Regression guard: this
        // service must never write that property, in either direction.
        var runner = new FakeProcessRunner();
        var service = new AccessibilityBusActivationService(runner);

        await service.SetActivatedAsync(true);
        await service.SetActivatedAsync(false);

        Assert.DoesNotContain(runner.Invocations, i => i.Args.Contains("ScreenReaderEnabled"));
    }

    [Fact]
    public async Task SetActivatedAsync_false_sets_IsEnabled_false()
    {
        var runner = new FakeProcessRunner();
        var service = new AccessibilityBusActivationService(runner);

        await service.SetActivatedAsync(false);

        var isEnabled = runner.Invocations.Single(i =>
            i.Args.Contains("set-property") && i.Args.Contains("IsEnabled")
        );
        Assert.Equal("false", isEnabled.Args[^1]);
    }

    [Fact]
    public async Task SetActivatedAsync_reports_failure_when_write_fails()
    {
        var runner = new FakeProcessRunner();
        runner.FailWhen((_, args) => args.Contains("set-property") && args.Contains("IsEnabled"));
        var service = new AccessibilityBusActivationService(runner);

        Assert.False(await service.SetActivatedAsync(true));
    }

    private static bool IsGetScreenReader(string file, IReadOnlyList<string> args) =>
        file == "busctl"
        && args.Contains("get-property")
        && args.Contains("ScreenReaderEnabled");

    [Fact]
    public async Task IsScreenReaderActiveAsync_parses_true()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(IsGetScreenReader, "b true\n");
        var service = new AccessibilityBusActivationService(runner);

        Assert.Equal(true, await service.IsScreenReaderActiveAsync());
    }

    [Fact]
    public async Task IsScreenReaderActiveAsync_parses_false()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(IsGetScreenReader, "b false\n");
        var service = new AccessibilityBusActivationService(runner);

        Assert.Equal(false, await service.IsScreenReaderActiveAsync());
    }

    [Fact]
    public async Task IsScreenReaderActiveAsync_returns_null_when_command_fails()
    {
        var runner = new FakeProcessRunner();
        runner.FailWhen(IsGetScreenReader, "bus unreachable");
        var service = new AccessibilityBusActivationService(runner);

        Assert.Null(await service.IsScreenReaderActiveAsync());
    }
}
