using TypeWhisper.Linux.Tests.Telemetry;
using TypeWhisper.PluginSDK;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PluginManagerDiagnosticsTests : IDisposable
{
    private const string FailingPluginId = "com.test.deactivate-fails";

    private readonly string _tempDir = TestPaths.CreateTempDirectory("TypeWhisper.PluginManagerDiagnosticsTests");

    public void Dispose() => TestPaths.DeleteDirectory(_tempDir);

    [Fact]
    public async Task FailedDisable_IsReportedWithThePluginIdOnly()
    {
        var reporter = new RecordingReporter();
        var plugin = new FailingDeactivatePlugin();
        var manager = TestPluginManagerFactory.Create(
            loadedPlugins: [TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, FailingPluginId, plugin)],
            activatedPluginIds: [FailingPluginId],
            diagnostics: reporter
        );

        await manager.DisablePluginAsync(FailingPluginId);

        Assert.True(manager.IsEnabled(FailingPluginId));
        var capture = Assert.Single(reporter.Captures);
        Assert.Same(plugin.Failure, capture.Exception);
        Assert.Equal("plugin.deactivate", capture.Operation);
        Assert.Equal(new Dictionary<string, string> { ["plugin.id"] = FailingPluginId }, capture.Tags);
    }

    private sealed class FailingDeactivatePlugin : ITypeWhisperPlugin
    {
        public InvalidOperationException Failure { get; } = new("secret deactivate detail");
        public string PluginId => FailingPluginId;
        public string PluginName => "Deactivate fails";
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

        public Task DeactivateAsync() => Task.FromException(Failure);

        public void Dispose() { }
    }
}
