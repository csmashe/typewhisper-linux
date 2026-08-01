using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class GnomeWindowCallsSetupHelperTests
{
    [Fact]
    public void IsCurrentlyInstalled_uses_bounded_discard_probes_in_endpoint_order()
    {
        var runner = new FakeProcessRunner();
        runner.FailWhen(
            (_, args) => args.Contains("/org/gnome/Shell/Extensions/Windows")
        );
        var helper = new GnomeWindowCallsSetupHelper(runner);

        Assert.True(helper.IsCurrentlyInstalled());

        AssertBothEndpointsProbed(runner);
    }

    [Fact]
    public void IsCurrentlyInstalled_treats_timed_out_probe_as_absent_and_tries_next_endpoint()
    {
        var runner = new FakeProcessRunner { Default = TimedOut() };
        runner.RespondWith(
            (_, args) => args.Contains("/org/gnome/Shell/Extensions/WindowsExt"),
            string.Empty
        );
        var helper = new GnomeWindowCallsSetupHelper(runner);

        Assert.True(helper.IsCurrentlyInstalled());

        AssertBothEndpointsProbed(runner);
    }

    [Fact]
    public void IsCurrentlyInstalled_is_false_when_every_probe_times_out()
    {
        var runner = new FakeProcessRunner { Default = TimedOut() };
        var helper = new GnomeWindowCallsSetupHelper(runner);

        Assert.False(helper.IsCurrentlyInstalled());

        AssertBothEndpointsProbed(runner);
    }

    [Fact]
    public void IsCurrentlyInstalled_treats_start_failure_as_absent_and_tries_next_endpoint()
    {
        var runner = new FakeProcessRunner
        {
            Default = FakeProcessRunner.NotStarted(),
        };
        runner.RespondWith(
            (_, args) => args.Contains("/org/gnome/Shell/Extensions/WindowsExt"),
            string.Empty
        );
        var helper = new GnomeWindowCallsSetupHelper(runner);

        Assert.True(helper.IsCurrentlyInstalled());

        AssertBothEndpointsProbed(runner);
    }

    [Fact]
    public void IsCurrentlyInstalled_is_false_when_every_probe_fails_to_start()
    {
        var runner = new FakeProcessRunner
        {
            Default = FakeProcessRunner.NotStarted(),
        };
        var helper = new GnomeWindowCallsSetupHelper(runner);

        Assert.False(helper.IsCurrentlyInstalled());

        AssertBothEndpointsProbed(runner);
    }

    [Fact]
    public void TryOpenInstallPage_uses_uri_launch()
    {
        var runner = new FakeProcessRunner();

        Assert.True(new GnomeWindowCallsSetupHelper(runner).TryOpenInstallPage());

        Assert.Equal(
            new Uri("https://extensions.gnome.org/extension/4974/window-calls/"),
            Assert.Single(runner.LaunchedUris)
        );
    }

    private static ProcessRunResult TimedOut()
    {
        return new ProcessRunResult(true, true, 0, string.Empty, string.Empty);
    }

    private static void AssertBothEndpointsProbed(FakeProcessRunner runner)
    {
        Assert.Collection(
            runner.SupervisorInvocations,
            first => AssertEndpoint(
                first,
                "/org/gnome/Shell/Extensions/Windows",
                "org.gnome.Shell.Extensions.Windows.List"
            ),
            second => AssertEndpoint(
                second,
                "/org/gnome/Shell/Extensions/WindowsExt",
                "org.gnome.Shell.Extensions.WindowsExt.List"
            )
        );
    }

    private static void AssertEndpoint(
        FakeProcessRunner.SupervisorInvocation invocation,
        string path,
        string method
    )
    {
        Assert.Equal("gdbus", invocation.Command.FileName);
        Assert.Equal(
            [
                "call",
                "--session",
                "--dest",
                "org.gnome.Shell",
                "--object-path",
                path,
                "--method",
                method,
            ],
            invocation.Command.Arguments
        );
        Assert.Equal(TimeSpan.FromSeconds(1), invocation.Options.Timeout);
        Assert.Equal(
            ProcessCaptureMode.Discard,
            invocation.Options.StandardOutput
        );
        Assert.Equal(
            ProcessCaptureMode.Discard,
            invocation.Options.StandardError
        );
    }
}
