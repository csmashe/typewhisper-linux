using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AppRelauncherTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory("TypeWhisper.AppRelauncherTests");

    public void Dispose() => TestPaths.DeleteDirectory(_tempDir);

    [Fact]
    public void An_AppImage_is_restarted_from_its_file_rather_than_its_mount()
    {
        var appImage = Path.Join(_tempDir, "TypeWhisper.AppImage");
        File.WriteAllText(appImage, "");

        var command = AppRelauncher.ResolveCommand(appImage, "/tmp/.mount_x/usr/bin/typewhisper", null);

        Assert.NotNull(command);
        Assert.Equal(appImage, command.FileName);
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public void A_missing_AppImage_falls_back_to_the_executable()
    {
        var command = AppRelauncher.ResolveCommand(Path.Join(_tempDir, "gone.AppImage"), "/usr/lib/typewhisper/typewhisper", null);

        Assert.NotNull(command);
        Assert.Equal("/usr/lib/typewhisper/typewhisper", command.FileName);
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public void A_development_run_restarts_the_dotnet_host_with_the_app_assembly()
    {
        var command = AppRelauncher.ResolveCommand(null, "/usr/share/dotnet/dotnet", "/src/TypeWhisper.Linux.dll");

        Assert.NotNull(command);
        Assert.Equal("/usr/share/dotnet/dotnet", command.FileName);
        Assert.Equal(["/src/TypeWhisper.Linux.dll"], command.Arguments);
    }

    [Fact]
    public void Without_an_executable_there_is_nothing_to_restart()
    {
        Assert.Null(AppRelauncher.ResolveCommand(null, null, null));
        Assert.Null(AppRelauncher.ResolveCommand(null, "/usr/share/dotnet/dotnet", null));
    }
}
