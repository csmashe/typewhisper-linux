using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CliInstallServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"tw-cli-test-{Guid.NewGuid():N}");
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    [Fact]
    public void GetState_reports_missing_bundle()
    {
        var service = new CliInstallService(
            () => null,
            () => Path.Combine(_tempDir, "install"),
            () => Path.Combine(_tempDir, "bin"));

        var state = service.GetState();

        Assert.False(state.BundledCliAvailable);
        Assert.False(state.Installed);
        Assert.Contains("not found", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_copies_payload_and_writes_launcher()
    {
        var sourceDir = Path.Combine(_tempDir, "bundle");
        var installDir = Path.Combine(_tempDir, "install");
        var launcherDir = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "typewhisper"), "apphost");
        File.WriteAllText(Path.Combine(sourceDir, "typewhisper.dll"), "dll");
        File.WriteAllText(Path.Combine(sourceDir, "typewhisper.runtimeconfig.json"), "{}");
        Environment.SetEnvironmentVariable("PATH", launcherDir);

        var service = new CliInstallService(
            () => Path.Combine(sourceDir, "typewhisper"),
            () => installDir,
            () => launcherDir);

        var state = service.Install();

        Assert.True(state.BundledCliAvailable);
        Assert.True(state.Installed);
        Assert.True(state.LauncherDirectoryInPath);
        Assert.True(File.Exists(Path.Combine(installDir, "typewhisper")));
        Assert.True(File.Exists(Path.Combine(installDir, "typewhisper.dll")));
        Assert.True(File.Exists(Path.Combine(installDir, "typewhisper.runtimeconfig.json")));
        Assert.Contains(Path.Combine(installDir, "typewhisper"), File.ReadAllText(Path.Combine(launcherDir, "typewhisper")));
    }

    [Fact]
    public void Examples_include_linux_bearer_token_setup()
    {
        var cli = CliInstallService.BuildCliExamples(9876);
        var curl = CliInstallService.BuildCurlExamples(9876);

        Assert.Contains(cli, command => command.Contains("TYPEWHISPER_API_TOKEN", StringComparison.Ordinal));
        Assert.Contains(curl, command => command.Contains("Authorization: Bearer $TYPEWHISPER_API_TOKEN", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
