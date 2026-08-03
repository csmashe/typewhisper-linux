using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CliInstallServiceTests : IDisposable
{
    private const UnixFileMode ExpectedExecutableMode =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherExecute;
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
    public void Install_copies_single_file_and_writes_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        string? verifiedPath = null;
        WriteBundle(sourceDir, "v1");
        File.WriteAllText(Path.Join(sourceDir, "typewhisper-cli.dll"), "must-not-copy");
        File.WriteAllText(
            Path.Join(sourceDir, "typewhisper-cli.runtimeconfig.json"),
            "must-not-copy"
        );
        Environment.SetEnvironmentVariable("PATH", launcherDir);

        var service = new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir,
            path =>
            {
                verifiedPath = path;
                return SuccessfulVerification(path);
            }
        );

        var state = service.Install();

        Assert.True(state.BundledCliAvailable);
        Assert.True(state.Installed);
        Assert.True(state.LauncherDirectoryInPath);
        Assert.Equal("apphost-v1", File.ReadAllText(Path.Join(installDir, "typewhisper-cli")));
        Assert.False(File.Exists(Path.Join(installDir, "typewhisper-cli.dll")));
        Assert.False(File.Exists(Path.Join(installDir, "typewhisper-cli.runtimeconfig.json")));
        Assert.Equal(
            ExpectedLauncher(Path.Join(installDir, "typewhisper-cli")),
            File.ReadAllText(Path.Join(launcherDir, "typewhisper-cli"))
        );
        Assert.NotNull(verifiedPath);
        Assert.StartsWith(
            Path.Join(installDir, ".typewhisper-cli."),
            verifiedPath,
            StringComparison.Ordinal
        );
        Assert.EndsWith(".tmp", verifiedPath, StringComparison.Ordinal);
        AssertNoTemporaryCliFiles(installDir);
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
        File.WriteAllText(Path.Join(installDir, "keep"), "sentinel");
        const string foreignLauncher =
            "#!/usr/bin/env sh\n# Installed by TypeWhisperer\nexec /other/tool \"$@\"";
        File.WriteAllText(launcherPath, foreignLauncher);

        var verificationCalled = false;
        var service = CreateService(
            sourceDir,
            installDir,
            launcherDir,
            _ =>
            {
                verificationCalled = true;
                throw new InvalidOperationException("Verification must not run.");
            }
        );

        var beforeInstall = service.GetState();
        var state = service.Install();

        Assert.False(beforeInstall.Installed);
        Assert.False(state.Installed);
        Assert.Equal(foreignLauncher, File.ReadAllText(launcherPath));
        Assert.Equal("old-apphost", File.ReadAllText(installPath));
        Assert.Equal("old-dll", File.ReadAllText(Path.Join(installDir, "typewhisper-cli.dll")));
        Assert.Equal("sentinel", File.ReadAllText(Path.Join(installDir, "keep")));
        Assert.False(File.Exists(Path.Join(installDir, "typewhisper-cli.runtimeconfig.json")));
        Assert.False(verificationCalled);
        AssertNoTemporaryCliFiles(installDir);
        Assert.Contains(launcherPath, state.StatusText, StringComparison.Ordinal);
        Assert.Contains("not managed", state.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untouched", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_refuses_unrecorded_private_binary_without_running_verification()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        WriteBundle(sourceDir, "new");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(installPath, "foreign-private-binary");
        var verificationCalled = false;
        var service = CreateService(
            sourceDir,
            installDir,
            launcherDir,
            _ =>
            {
                verificationCalled = true;
                return SuccessfulVerification(installPath);
            }
        );

        var state = service.Install();

        Assert.False(state.Installed);
        Assert.False(verificationCalled);
        Assert.Equal("foreign-private-binary", File.ReadAllText(installPath));
        Assert.False(File.Exists(Path.Join(launcherDir, "typewhisper-cli")));
        Assert.Contains(installPath, state.StatusText, StringComparison.Ordinal);
        Assert.Contains("not managed", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_refuses_customized_recorded_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "v1");
        var service = CreateService(sourceDir, installDir, launcherDir);
        Assert.True(service.Install().Installed);
        File.AppendAllText(launcherPath, "# user customization\n");

        var state = service.Install();

        Assert.False(state.Installed);
        Assert.EndsWith("# user customization\n", File.ReadAllText(launcherPath));
        Assert.Contains("not managed", state.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_atomically_replaces_owned_install()
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
        var sawOldInstallDuringVerification = false;
        service = CreateService(
            sourceDir,
            installDir,
            launcherDir,
            path =>
            {
                sawOldInstallDuringVerification =
                    File.ReadAllText(installPath) == "apphost-v1"
                    && File.ReadAllText(path) == "apphost-v2";
                return SuccessfulVerification(path);
            }
        );

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.True(sawOldInstallDuringVerification);
        Assert.Equal("apphost-v2", File.ReadAllText(installPath));
        Assert.Equal(ExpectedLauncher(installPath), File.ReadAllText(launcherPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_sets_and_verifies_executable_mode_before_verification()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var modeWasRead = false;
        UnixFileMode? verifiedMode = null;
        WriteBundle(sourceDir, "v1");
        var service = CreateService(
            sourceDir,
            installDir,
            launcherDir,
            path =>
            {
                Assert.True(modeWasRead);
                return SuccessfulVerification(path);
            },
            path =>
            {
                modeWasRead = true;
                if (!OperatingSystem.IsLinux())
                {
                    throw new PlatformNotSupportedException();
                }

                var mode = File.GetUnixFileMode(path);
                verifiedMode = mode;
                return mode;
            }
        );

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal(ExpectedExecutableMode, verifiedMode);
        Assert.Equal(
            ExpectedExecutableMode,
            File.GetUnixFileMode(Path.Join(installDir, "typewhisper-cli"))
        );
    }

    // First upgrade after the manifest lands: the old binary predates recorded state and
    // its bytes differ from the new bundle, so the launcher's marker is the only evidence it's ours.
    [Fact]
    public void Install_upgrades_a_pre_manifest_binary_vouched_for_by_our_launcher()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "new");
        WriteBundle(installDir, "old");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(launcherPath, ExpectedLauncher(installPath));
        var service = CreateService(sourceDir, installDir, launcherDir);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal("apphost-new", File.ReadAllText(installPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_refuses_a_pre_manifest_binary_with_no_launcher_vouching_for_it()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        WriteBundle(sourceDir, "new");
        WriteBundle(installDir, "foreign");
        var service = CreateService(sourceDir, installDir, launcherDir);

        service.Install();

        Assert.Equal("apphost-foreign", File.ReadAllText(installPath));
    }

    [Fact]
    public void Install_repairs_an_installed_cli_whose_execute_bit_was_removed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        WriteBundle(sourceDir, "v1");
        WriteBundle(installDir, "v1");
        // Same bytes as the bundle, but stripped of execute: reporting this as
        // installed would leave the user with a CLI that cannot run.
        File.SetUnixFileMode(installPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var service = CreateService(sourceDir, installDir, launcherDir, SuccessfulVerification);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal(ExpectedExecutableMode, File.GetUnixFileMode(installPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_executable_mode_verification_failure_preserves_old_cli()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        var versionVerificationCalled = false;
        WriteBundle(sourceDir, "old");
        CreateService(sourceDir, installDir, launcherDir).Install();
        WriteBundle(sourceDir, "new");
        var service = CreateService(
            sourceDir,
            installDir,
            launcherDir,
            path =>
            {
                versionVerificationCalled = true;
                return SuccessfulVerification(path);
            },
            _ => UnixFileMode.UserRead
        );

        var error = Assert.Throws<InvalidOperationException>(service.Install);

        Assert.Contains("mode verification failed", error.Message, StringComparison.Ordinal);
        Assert.False(versionVerificationCalled);
        Assert.Equal("apphost-old", File.ReadAllText(installPath));
        Assert.Equal(ExpectedLauncher(installPath), File.ReadAllText(launcherPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_verification_failure_preserves_old_cli_and_removes_temp()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "old");
        CreateService(sourceDir, installDir, launcherDir).Install();
        WriteBundle(sourceDir, "new");
        var service = CreateService(
            sourceDir,
            installDir,
            launcherDir,
            _ => new CliInstallService.CliVerificationResult(17, "", "loader failure")
        );

        var error = Assert.Throws<InvalidOperationException>(service.Install);

        Assert.Contains("exit code 17", error.Message, StringComparison.Ordinal);
        Assert.Equal("apphost-old", File.ReadAllText(installPath));
        Assert.Equal(ExpectedLauncher(installPath), File.ReadAllText(launcherPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_version_mismatch_preserves_old_cli_and_removes_temp()
    {
        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        var launcherPath = Path.Join(launcherDir, "typewhisper-cli");
        WriteBundle(sourceDir, "old");
        CreateService(sourceDir, installDir, launcherDir).Install();
        WriteBundle(sourceDir, "new");
        var service = CreateService(
            sourceDir,
            installDir,
            launcherDir,
            _ =>
                new CliInstallService.CliVerificationResult(
                    0,
                    "typewhisper-cli 999.0.0\n",
                    ""
                )
        );

        var error = Assert.Throws<InvalidOperationException>(service.Install);

        Assert.Contains("expected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AppVersion.Display, error.Message, StringComparison.Ordinal);
        Assert.Equal("apphost-old", File.ReadAllText(installPath));
        Assert.Equal(ExpectedLauncher(installPath), File.ReadAllText(launcherPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_same_source_and_target_does_not_truncate_cli()
    {
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        WriteBundle(installDir, "same");
        // A genuinely already-installed CLI carries the executable mode we publish;
        // a 0644 file here is drift the installer is now expected to repair.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(installPath, ExpectedExecutableMode);
        }

        var verificationCalled = false;
        var service = CreateService(
            installDir,
            installDir,
            launcherDir,
            _ =>
            {
                verificationCalled = true;
                throw new InvalidOperationException("Verification must not run.");
            }
        );

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.False(verificationCalled);
        Assert.Equal("apphost-same", File.ReadAllText(installPath));
        Assert.Equal(
            ExpectedLauncher(installPath),
            File.ReadAllText(Path.Join(launcherDir, "typewhisper-cli"))
        );
        AssertNoTemporaryCliFiles(installDir);
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
        Assert.DoesNotContain(
            cli,
            command => command.Contains("--port", StringComparison.Ordinal)
        );
        Assert.Contains(
            curl,
            command =>
                command.Contains(
                    "Authorization: Bearer $TYPEWHISPER_API_TOKEN",
                    StringComparison.Ordinal
                )
        );
        Assert.Contains(
            curl,
            command =>
                command.Contains("localhost:9876", StringComparison.Ordinal)
                && command.Contains(
                    "Authorization: Bearer $TYPEWHISPER_API_TOKEN",
                    StringComparison.Ordinal
                )
        );
    }

    // The tests below leave verificationRunner at its default so the real
    // RunCliVerification runs against a script fixture — a real process
    // launch, args, and stdout/stderr capture — instead of a stub.

    [Fact]
    public void Install_real_verifier_runs_cli_with_version_argument()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        // Exits 64 unless "--version" is the sole argument, so a runner that forgot
        // the argument (or passed extras) fails verification instead of passing.
        WriteExecutableBundle(
            sourceDir,
            $"""
            #!/bin/sh
            [ "$#" -eq 1 ] || exit 64
            [ "$1" = "--version" ] || exit 64
            printf 'typewhisper-cli %s\n' '{AppVersion.Display}'
            """
        );
        var service = new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir
        );

        var state = service.Install();

        Assert.True(state.Installed);
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_real_verifier_surfaces_exit_code_and_stderr()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        WriteExecutableBundle(
            sourceDir,
            $"""
            #!/bin/sh
            printf 'typewhisper-cli %s\n' '{AppVersion.Display}'
            """
        );
        new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir
        ).Install();
        var oldBytes = File.ReadAllBytes(installPath);
        WriteExecutableBundle(
            sourceDir,
            """
            #!/bin/sh
            echo 'loader failure' >&2
            exit 3
            """
        );
        var service = new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir
        );

        var error = Assert.Throws<InvalidOperationException>(service.Install);

        Assert.Contains("exit code 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("loader failure", error.Message, StringComparison.Ordinal);
        Assert.Equal(oldBytes, File.ReadAllBytes(installPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    [Fact]
    public void Install_real_verifier_times_out_when_output_pipe_stays_open()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var sourceDir = Path.Join(_tempDir, "bundle");
        var installDir = Path.Join(_tempDir, "install");
        var launcherDir = Path.Join(_tempDir, "bin");
        var installPath = Path.Join(installDir, "typewhisper-cli");
        // The backgrounded child inherits stdout, so the pipe stays open after the
        // script itself exits 0. Waiting only on process exit would then block in
        // ReadToEnd forever; the deadline has to cover the reads too.
        WriteExecutableBundle(
            sourceDir,
            $"""
            #!/bin/sh
            printf 'typewhisper-cli %s\n' '{AppVersion.Display}'
            """
        );
        new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir
        ).Install();
        var oldBytes = File.ReadAllBytes(installPath);
        WriteExecutableBundle(
            sourceDir,
            $"""
            #!/bin/sh
            sleep 30 &
            printf 'typewhisper-cli %s\n' '{AppVersion.Display}'
            exit 0
            """
        );
        var service = new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir,
            path => CliInstallService.RunCliVerification(path, TimeSpan.FromSeconds(2))
        );

        Assert.Throws<TimeoutException>(service.Install);

        Assert.Equal(oldBytes, File.ReadAllBytes(installPath));
        AssertNoTemporaryCliFiles(installDir);
    }

    private static void WriteExecutableBundle(string sourceDir, string script)
    {
        Directory.CreateDirectory(sourceDir);
        var path = Path.Join(sourceDir, "typewhisper-cli");
        File.WriteAllText(path, script + "\n");
    }

    private static CliInstallService CreateService(
        string sourceDir,
        string installDir,
        string launcherDir,
        Func<string, CliInstallService.CliVerificationResult>? verificationRunner = null,
        Func<string, UnixFileMode>? unixFileModeReader = null
    )
    {
        return new CliInstallService(
            () => Path.Join(sourceDir, "typewhisper-cli"),
            () => installDir,
            () => launcherDir,
            verificationRunner ?? SuccessfulVerification,
            unixFileModeReader
        );
    }

    private static void WriteBundle(string sourceDir, string version)
    {
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Join(sourceDir, "typewhisper-cli"), $"apphost-{version}");
    }

    private static CliInstallService.CliVerificationResult SuccessfulVerification(string path)
    {
        Assert.True(File.Exists(path));
        return new CliInstallService.CliVerificationResult(
            0,
            $"typewhisper-cli {AppVersion.Display}\n",
            ""
        );
    }

    private static void AssertNoTemporaryCliFiles(string installDir)
    {
        Assert.Empty(Directory.EnumerateFiles(installDir, ".typewhisper-cli.*.tmp"));
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
