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

    [Fact]
    public void EnsureIsolated_AcceptsPathOutsideProductionRoot()
    {
        // The guard is worthless if it also rejects the temp paths every test actually uses.
        var path = Path.Join(Path.GetTempPath(), $"TypeWhisper.Isolation-{Guid.NewGuid():N}");

        Assert.Equal(Path.GetFullPath(path), TestPaths.EnsureIsolated(path));
    }
}
