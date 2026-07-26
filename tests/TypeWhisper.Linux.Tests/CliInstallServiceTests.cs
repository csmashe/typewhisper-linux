using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CliInstallServiceTests : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    private readonly string _tempDir = TestPaths.CreateTempDirectory("tw-cli-test");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
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
        WriteBundle(sourceDir, "v1");
        Environment.SetEnvironmentVariable("PATH", launcherDir);

        var service = new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir
        );

        var state = service.Install();

        Assert.True(state.BundledCliAvailable);
        Assert.True(state.Installed);
        Assert.True(state.LauncherDirectoryInPath);
        Assert.True(File.Exists(Path.Join(installDir, "typewhisper-cli")));
        Assert.True(File.Exists(Path.Join(installDir, "typewhisper-cli.dll")));
        Assert.True(File.Exists(Path.Join(installDir, "typewhisper-cli.runtimeconfig.json")));
        Assert.Equal(
            ExpectedLauncher(Path.Join(installDir, "typewhisper-cli")),
            File.ReadAllText(Path.Join(launcherDir, "typewhisper-cli"))
        );
        Assert.False(File.Exists(Path.Join(installDir, "typewhisper")));
        Assert.False(File.Exists(Path.Join(launcherDir, "typewhisper")));
    }

    [Fact]
    public void Install_preserves_and_reports_foreign_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "new");
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(installPath, "old-apphost");
        File.WriteAllText(Path.Join(installDir, "typewhisper-cli.dll"), "old-dll");
        const string foreignLauncher =
            "#!/usr/bin/env sh\n# Installed by TypeWhisperer\nexec /other/tool \"$@\"";
        File.WriteAllText(launcherPath, foreignLauncher);

        var service = CreateService(sourceDir, installDir, launcherDir);

        var beforeInstall = service.GetState();
        var state = service.Install();

        Assert.False(beforeInstall.Installed);
        Assert.False(state.Installed);
        Assert.Equal(foreignLauncher, File.ReadAllText(launcherPath));
        Assert.Equal("old-apphost", File.ReadAllText(installPath));
        Assert.Equal("old-dll", File.ReadAllText(Path.Join(installDir, "typewhisper-cli.dll")));
        Assert.False(File.Exists(Path.Join(installDir, "typewhisper-cli.runtimeconfig.json")));
        Assert.Contains(launcherPath, state.StatusText, StringComparison.Ordinal);
        Assert.Contains("not managed", state.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untouched", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_updates_owned_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "v1");
        var service = CreateService(sourceDir, installDir, launcherDir);
        service.Install();
        WriteBundle(sourceDir, "v2");

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal("apphost-v2", File.ReadAllText(installPath));
        Assert.Equal("dll-v2", File.ReadAllText(Path.Join(installDir, "typewhisper-cli.dll")));
        Assert.Equal(ExpectedLauncher(installPath), File.ReadAllText(launcherPath));
    }

    [Fact]
    public void Install_updates_and_marks_legacy_owned_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "v2");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(launcherPath, ExpectedLegacyLauncher(installPath));
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal("apphost-v2", File.ReadAllText(installPath));
        Assert.Equal(ExpectedLauncher(installPath), File.ReadAllText(launcherPath));
    }

    [Fact]
    public void Install_preserves_legacy_lookalike_with_different_exec_target()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherDir);
        var foreignLauncher = ExpectedLegacyLauncher(
            Path.Join(_tempDir, "other", "typewhisper-cli")
        );
        File.WriteAllText(launcherPath, foreignLauncher);
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.False(state.Installed);
        Assert.Equal(foreignLauncher, File.ReadAllText(launcherPath));
        Assert.False(Directory.Exists(installDir));
        Assert.Contains("not managed", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_preserves_symlink_launcher_and_target()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        var linkTarget = Path.Join(_tempDir, "package-typewhisper-cli");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(linkTarget, ExpectedLauncher(Path.Join(installDir, "typewhisper-cli")));
        File.CreateSymbolicLink(launcherPath, linkTarget);
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.False(state.Installed);
        Assert.Equal(linkTarget, new FileInfo(launcherPath).LinkTarget);
        Assert.Equal(
            ExpectedLauncher(Path.Join(installDir, "typewhisper-cli")),
            File.ReadAllText(linkTarget)
        );
        Assert.False(Directory.Exists(installDir));
        Assert.Contains("untouched", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_retires_marked_legacy_named_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var legacyLauncherPath = Path.Join(launcherDir, "typewhisper");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(
            legacyLauncherPath,
            ExpectedLauncher(Path.Join(installDir, "typewhisper"))
        );
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.False(File.Exists(legacyLauncherPath));
        Assert.True(File.Exists(Path.Join(launcherDir, "typewhisper-cli")));
    }

    [Fact]
    public void Install_retires_unmarked_legacy_named_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var legacyLauncherPath = Path.Join(launcherDir, "typewhisper");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(
            legacyLauncherPath,
            ExpectedLegacyLauncher(Path.Join(installDir, "typewhisper"))
        );
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.False(File.Exists(legacyLauncherPath));
    }

    [Fact]
    public void Install_preserves_foreign_legacy_named_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var legacyLauncherPath = Path.Join(launcherDir, "typewhisper");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherDir);
        // Points somewhere else entirely, so it is not the launcher this service wrote.
        var foreignLauncher = ExpectedLegacyLauncher(Path.Join(_tempDir, "other", "typewhisper"));
        File.WriteAllText(legacyLauncherPath, foreignLauncher);
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal(foreignLauncher, File.ReadAllText(legacyLauncherPath));
    }

    [Fact]
    public void Install_preserves_desktop_app_symlink_at_legacy_name()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var legacyLauncherPath = Path.Join(launcherDir, "typewhisper");
        // The tarball installer symlinks ~/.local/bin/typewhisper at the desktop app.
        var desktopAppPath = Path.Join(_tempDir, "app", "typewhisper");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherDir);
        Directory.CreateDirectory(Path.Join(_tempDir, "app"));
        File.WriteAllText(desktopAppPath, "desktop-app");
        File.CreateSymbolicLink(legacyLauncherPath, desktopAppPath);
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal(desktopAppPath, new FileInfo(legacyLauncherPath).LinkTarget);
        Assert.Equal("desktop-app", File.ReadAllText(desktopAppPath));
    }

    [Fact]
    public void Install_preserves_differently_cased_foreign_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherDir);
        var aliasPath = Path.Join(launcherDir, "TypeWhisper-Cli");
        const string foreignLauncher = "#!/usr/bin/env sh\nexec /other/tool \"$@\"";
        File.WriteAllText(aliasPath, foreignLauncher);
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.False(state.Installed);
        Assert.Equal(foreignLauncher, File.ReadAllText(aliasPath));
        Assert.False(Directory.Exists(installDir));
        Assert.Contains("not managed", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_preserves_directory_at_launcher_path()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "v1");
        Directory.CreateDirectory(launcherPath);
        File.WriteAllText(Path.Join(launcherPath, "keep"), "content");
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.False(state.Installed);
        Assert.True(Directory.Exists(launcherPath));
        Assert.Equal("content", File.ReadAllText(Path.Join(launcherPath, "keep")));
        Assert.False(Directory.Exists(installDir));
        Assert.Contains("not managed", state.StatusText, StringComparison.OrdinalIgnoreCase);
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
        Assert.All(
            cli.Skip(1),
            command => Assert.StartsWith("typewhisper-cli ", command, StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            cli,
            command => command.StartsWith("typewhisper ", StringComparison.Ordinal)
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

    private static CliInstallService CreateService(
        string sourceDir,
        string installDir,
        string launcherDir
    )
    {
        return new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir
        );
    }

    private static void WriteBundle(string sourceDir, string version)
    {
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Join(sourceDir, "typewhisper-cli"), $"apphost-{version}");
        File.WriteAllText(Path.Join(sourceDir, "typewhisper-cli.dll"), $"dll-{version}");
        File.WriteAllText(
            Path.Join(sourceDir, "typewhisper-cli.runtimeconfig.json"),
            $"{{\"version\":\"{version}\"}}"
        );
    }

    private static string ExpectedLauncher(string installPath)
    {
        return $"#!/usr/bin/env sh\n# Installed by TypeWhisper\nexec \"{installPath}\" \"$@\"";
    }

    private static string ExpectedLegacyLauncher(string installPath)
    {
        return $"#!/usr/bin/env sh\nexec \"{installPath}\" \"$@\"";
    }
}
