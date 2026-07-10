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
    public async Task SetActivatedAsync_true_sets_both_flags_true()
    {
        var runner = new FakeProcessRunner();
        var service = new AccessibilityBusActivationService(runner);

        var ok = await service.SetActivatedAsync(true);

        Assert.True(ok);
        var setCalls = runner
            .Invocations.Where(i => i.FileName == "busctl" && i.Args.Contains("set-property"))
            .ToList();
        Assert.Equal(2, setCalls.Count);
        Assert.Contains(
            setCalls,
            c => c.Args.Contains("IsEnabled") && c.Args[^2] == "b" && c.Args[^1] == "true"
        );
        Assert.Contains(
            setCalls,
            c => c.Args.Contains("ScreenReaderEnabled") && c.Args[^1] == "true"
        );
    }

    [Fact]
    public async Task SetActivatedAsync_false_sets_flags_false()
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
    public async Task SetActivatedAsync_reports_failure_when_primary_write_fails()
    {
        var runner = new FakeProcessRunner();
        runner.FailWhen((_, args) => args.Contains("set-property") && args.Contains("IsEnabled"));
        var service = new AccessibilityBusActivationService(runner);

        Assert.False(await service.SetActivatedAsync(true));
    }

    [Fact]
    public async Task SetActivatedAsync_true_skips_screen_reader_write_when_primary_write_fails()
    {
        // ScreenReaderEnabled alone is useless and would orphan global state we reported as failed.
        var runner = new FakeProcessRunner();
        runner.FailWhen((_, args) => args.Contains("set-property") && args.Contains("IsEnabled"));
        var service = new AccessibilityBusActivationService(runner);

        await service.SetActivatedAsync(true);

        Assert.DoesNotContain(
            runner.Invocations,
            i => i.Args.Contains("set-property") && i.Args.Contains("ScreenReaderEnabled")
        );
    }

}
