using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.PluginSDK.Processes;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AtSpiUrlExtractorProcessTests
{
    [Fact]
    public void Constructor_and_walk_use_discrete_bounded_supervisor_probes()
    {
        var runner = new FakeProcessRunner
        {
            Default = new ProcessRunResult(
                true,
                false,
                0,
                "('unix:path=/tmp/a11y-test',)\n",
                string.Empty
            ),
        };
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.Collection(
            runner.SupervisorInvocations,
            busctl => AssertProbe(busctl, "busctl", ["--version"], true),
            gdbus => AssertProbe(gdbus, "gdbus", ["help"], true)
        );
        runner.SupervisorInvocations.Clear();

        Assert.Null(extractor.TryGetBrowserUrl("firefox", "Example"));

        var address = Assert.Single(
            runner.SupervisorInvocations,
            invocation => invocation.Command.FileName == "gdbus"
        );
        AssertProbe(
            address,
            "gdbus",
            [
                "call",
                "--session",
                "--dest",
                "org.a11y.Bus",
                "--object-path",
                "/org/a11y/bus",
                "--method",
                "org.a11y.Bus.GetAddress",
            ],
            false
        );
        Assert.All(
            runner.SupervisorInvocations,
            invocation => Assert.Equal(
                TimeSpan.FromSeconds(1),
                invocation.Options.Timeout
            )
        );
    }

    // ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local -- expected values for an
    // assertion helper; asserting on them is the whole point
    private static void AssertProbe(
        FakeProcessRunner.SupervisorInvocation invocation,
        string fileName,
        IReadOnlyList<string> arguments,
        bool discarded
    )
    {
        Assert.Equal(fileName, invocation.Command.FileName);
        Assert.Equal(arguments, invocation.Command.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(1), invocation.Options.Timeout);
        Assert.Equal(
            discarded ? ProcessCaptureMode.Discard : ProcessCaptureMode.Utf8Text,
            invocation.Options.StandardOutput
        );
        Assert.Equal(
            ProcessCaptureMode.Discard,
            invocation.Options.StandardError
        );
    }
}
