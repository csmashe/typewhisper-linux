using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CliInstallServiceTests : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    private readonly string _tempDir = Path.Join(
        Path.GetTempPath(),
        $"tw-cli-test-{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void GetState_reports_missing_bundle()
    {
        var service = new CliInstallService(
            () => null,
            () => Path.Join(_tempDir, "install"),
            () => Path.Join(_tempDir, "bin")
        );

        var state = service.GetState();

        Assert.False(state.BundledCliAvailable);
        Assert.False(state.Installed);
        Assert.Contains("not found", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_copies_payload_and_writes_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Join(sourceDir, "typewhisper"), "apphost");
        File.WriteAllText(Path.Join(sourceDir, "typewhisper.dll"), "dll");
        File.WriteAllText(Path.Join(sourceDir, "typewhisper.runtimeconfig.json"), "{}");
        Environment.SetEnvironmentVariable("PATH", launcherDir);

        var service = new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper"),
            () => installDir,
            () => launcherDir
        );

        var state = service.Install();

        Assert.True(state.BundledCliAvailable);
        Assert.True(state.Installed);
        Assert.True(state.LauncherDirectoryInPath);
        Assert.True(File.Exists(Path.Join(installDir, "typewhisper")));
        Assert.True(File.Exists(Path.Join(installDir, "typewhisper.dll")));
        Assert.True(File.Exists(Path.Join(installDir, "typewhisper.runtimeconfig.json")));
        Assert.Contains(
            Path.Join(installDir, "typewhisper"),
            File.ReadAllText(Path.Join(launcherDir, "typewhisper"))
        );
    }

    [Fact]
    public void Examples_include_linux_bearer_token_setup()
    {
        var cli = CliInstallService.BuildCliExamples(9876);
        var curl = CliInstallService.BuildCurlExamples(9876);

        Assert.Contains(
            cli,
            command => command.Contains("TYPEWHISPER_API_TOKEN", StringComparison.Ordinal)
        );
        Assert.Contains(
            curl,
            command =>
                command.Contains(
                    "Authorization: Bearer $TYPEWHISPER_API_TOKEN",
                    StringComparison.Ordinal
                )
        );
    }
}