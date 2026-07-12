using TypeWhisper.Core;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TestPathsTests
{
    [Fact]
    public void EnsureIsolated_RejectsProductionRoot()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestPaths.EnsureIsolated(TypeWhisperEnvironment.BasePath)
        );
    }

    [Fact]
    public void EnsureIsolated_RejectsPathUnderProductionRoot()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TestPaths.EnsureIsolated(Path.Join(TypeWhisperEnvironment.BasePath, "Audio"))
        );
    }
}
