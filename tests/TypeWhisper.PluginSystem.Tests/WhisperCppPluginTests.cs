using Moq;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class WhisperCppPluginTests
{
    [Fact]
    public void SupportedAccelerationBackends_IsCpuAndNvidiaCuda()
    {
        var plugin = new WhisperCppPlugin();

        Assert.Contains(TranscriptionAccelerationBackend.Cpu, plugin.SupportedAccelerationBackends);
        Assert.Contains(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.SupportedAccelerationBackends);
    }

    [Fact]
    public void DefaultAccelerationStatus_ReportsCpu()
    {
        var plugin = new WhisperCppPlugin();

        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public void SetAccelerationPreference_Cpu_TracksPreference()
    {
        var plugin = new WhisperCppPlugin();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(TranscriptionAccelerationPreference.Cpu, plugin.AccelerationPreference);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
    }

    [Fact]
    public void SetAccelerationPreference_NvidiaCuda_TracksPreferenceAndShowsPending()
    {
        var plugin = new WhisperCppPlugin();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            plugin.AccelerationPreference);
        Assert.Equal(
            TranscriptionAccelerationBackend.NvidiaCuda,
            plugin.AccelerationStatus.ActiveBackend);
    }
}

public class SherpaOnnxPluginTests
{
    [Fact]
    public void SupportedAccelerationBackends_IsCpuOnly()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();

        Assert.Single(plugin.SupportedAccelerationBackends);
        Assert.Equal(
            TranscriptionAccelerationBackend.Cpu,
            plugin.SupportedAccelerationBackends[0]);
    }

    [Fact]
    public async Task SetAccelerationPreference_NvidiaCuda_LogsWarningAndStaysCpu()
    {
        var plugin = new TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin();
        var host = CreateHost(out var logEntries);
        await plugin.ActivateAsync(host);

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(
            TranscriptionAccelerationPreference.NvidiaCuda,
            plugin.AccelerationPreference);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
        Assert.NotNull(plugin.AccelerationStatus.Detail);
        Assert.Contains(
            logEntries,
            entry => entry.level == PluginLogLevel.Warning
                && entry.message.Contains("CUDA", StringComparison.OrdinalIgnoreCase));
    }

    private static IPluginHostServices CreateHost(
        out List<(PluginLogLevel level, string message)> logEntries)
    {
        var entries = new List<(PluginLogLevel, string)>();
        logEntries = entries;

        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(Path.GetTempPath());
        host.Setup(h => h.Log(It.IsAny<PluginLogLevel>(), It.IsAny<string>()))
            .Callback<PluginLogLevel, string>((lvl, msg) => entries.Add((lvl, msg)));
        return host.Object;
    }
}
