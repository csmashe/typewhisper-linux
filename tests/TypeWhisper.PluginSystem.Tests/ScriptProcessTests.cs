using Moq;
using TypeWhisper.Linux.Services;
using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.ProcessTestChild;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ScriptProcessTests : IDisposable
{
    private readonly string _directory =
        TestPaths.CreateTempDirectory("script-process");

    [Fact]
    public async Task RunScriptsAsync_handles_output_pressure_before_large_stdin()
    {
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(value => value.PluginDataDirectory).Returns(_directory);
        host.SetupGet(value => value.Processes).Returns(new ProcessRunner());
        var service = new ScriptService(host.Object);
        service.AddScript(
            new ScriptEntry
            {
                Name = "pressure",
                Shell = "bash",
                Command =
                    $"dotnet \"{typeof(ProcessTestChildMarker).Assembly.Location}\" pressure 131072",
            }
        );
        var input = new string('i', 1024 * 1024);

        var result = await service.RunScriptsAsync(
            input,
            new PostProcessingContext
            {
                ActiveAppName = "Editor",
                SourceLanguage = "en",
                ProfileName = "Default",
            },
            CancellationToken.None
        );

        Assert.StartsWith(new string('x', 1024), result);
        Assert.Contains($"{input.Length}:", result, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_directory);
    }
}
