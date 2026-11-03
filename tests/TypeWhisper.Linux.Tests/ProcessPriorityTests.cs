using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProcessPriorityTests
{
    [Fact]
    public void ResetToDefaults_uses_bounded_discrete_argv_probes()
    {
        var runner = new FakeProcessRunner();

        var result = ProcessPriority.ResetToDefaults(runner);

        Assert.Equal("renice: ok; ionice: ok", result);
        Assert.Collection(
            runner.SupervisorInvocations,
            invocation =>
            {
                Assert.Equal("renice", invocation.Command.FileName);
                Assert.Equal(
                    ["-n", "0", "-p", Environment.ProcessId.ToString()],
                    invocation.Command.Arguments
                );
                Assert.Equal(TimeSpan.FromSeconds(1), invocation.Options.Timeout);
            },
            invocation =>
            {
                Assert.Equal("ionice", invocation.Command.FileName);
                Assert.Equal(
                    ["-c", "2", "-n", "4", "-p", Environment.ProcessId.ToString()],
                    invocation.Command.Arguments
                );
                Assert.Equal(TimeSpan.FromSeconds(1), invocation.Options.Timeout);
            }
        );
    }
}
