extern alias SherpaOnnx;
using TypeWhisper.Plugin.ParakeetCtc;
using Xunit;
using SherpaOnnxPlugin = SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxCpuThreadsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(12, 4)]
    [InlineData(16, 4)]
    [InlineData(32, 4)]
    [InlineData(64, 4)]
    public void SherpaOnnx_ResolveDefaultCpuThreads_DefaultsToClampedSweetSpot(int processorCount, int expectedThreads)
    {
        Assert.Equal(expectedThreads, SherpaOnnxPlugin.ResolveDefaultCpuThreads(processorCount, envOverride: null));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 4)]
    public void NemoCtcModel_ResolveDefaultCpuThreads_DefaultsToClampedSweetSpot(int processorCount, int expectedThreads)
    {
        Assert.Equal(expectedThreads, NemoCtcModel.ResolveDefaultCpuThreads(processorCount, envOverride: null));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("6", 6)]
    [InlineData("8", 8)]
    [InlineData("12", 12)]
    public void SherpaOnnx_ResolveDefaultCpuThreads_HonorsEnvironmentVariable(string envValue, int expectedThreads)
    {
        Assert.Equal(expectedThreads, SherpaOnnxPlugin.ResolveDefaultCpuThreads(16, envOverride: envValue));
    }

    [Theory]
    [InlineData("0", 4)]
    [InlineData("-1", 4)]
    [InlineData("notanumber", 4)]
    [InlineData("", 4)]
    [InlineData("   ", 4)]
    public void SherpaOnnx_ResolveDefaultCpuThreads_IgnoresInvalidEnvironmentVariable(string envValue, int expectedThreads)
    {
        Assert.Equal(expectedThreads, SherpaOnnxPlugin.ResolveDefaultCpuThreads(16, envOverride: envValue));
    }

    [Fact]
    public void NemoCtcModel_ResolveDefaultCpuThreads_HonorsEnvironmentVariable()
    {
        Assert.Equal(7, NemoCtcModel.ResolveDefaultCpuThreads(16, envOverride: "7"));
        Assert.Equal(4, NemoCtcModel.ResolveDefaultCpuThreads(16, envOverride: "invalid"));
    }
}
