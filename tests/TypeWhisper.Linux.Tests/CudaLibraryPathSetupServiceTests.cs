// ReSharper disable MethodHasAsyncOverload -- synchronous File.ReadAll* is deliberate in these test assertions.
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.ManagedArtifacts;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CudaLibraryPathSetupServiceTests : IDisposable
{
    private const string CudaPath = "/opt/cuda-12/lib64";
    private const UnixFileMode Mode0600 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _root = TestPaths.CreateTempDirectory("cuda-path-setup");

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_root);
        }
        catch
        {
            // Best-effort cleanup for symlink and interruption fixtures.
        }
    }

    [Fact]
    public async Task Setup_refuses_foreign_environment_file_before_touching_shell_profile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EnvironmentPath)!);
        await File.WriteAllTextAsync(EnvironmentPath, "USER_SETTING=keep\n");

        var result = await CreateService().SetUpAsync();

        Assert.False(result.Success);
        Assert.Equal(
            CudaLibraryPathSetupFailure.EnvironmentFileRefused,
            result.Failure
        );
        Assert.Equal(
            ManagedFileClassification.Foreign,
            result.EnvironmentClassification
        );
        Assert.Equal("USER_SETTING=keep\n", File.ReadAllText(EnvironmentPath));
        Assert.False(File.Exists(BashProfile));
    }

    [Fact]
    public async Task Setup_refuses_environment_file_that_appears_after_probe()
    {
        // The shell provider runs between the environment probe and InstallAsync, so a
        // file planted here reproduces the probe/install race the second gate covers.
        var service = CreateService(shellProvider: () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EnvironmentPath)!);
            File.WriteAllText(EnvironmentPath, "USER_SETTING=raced\n");
            return "/bin/bash";
        });

        var result = await service.SetUpAsync();

        Assert.False(result.Success);
        Assert.Equal(CudaLibraryPathSetupFailure.EnvironmentFileRefused, result.Failure);
        Assert.Equal("USER_SETTING=raced\n", File.ReadAllText(EnvironmentPath));
        Assert.False(File.Exists(BashProfile));
    }

    [Fact]
    public async Task Customized_published_environment_file_is_refused_and_preserved()
    {
        var service = CreateService();
        Assert.True((await service.SetUpAsync()).Success);
        await File.AppendAllTextAsync(EnvironmentPath, "USER_CUSTOMIZATION=1\n");
        var shellBefore = File.ReadAllText(BashProfile);

        var update = await service.SetUpAsync();
        var removal = await service.RemoveAsync();

        Assert.False(update.Success);
        Assert.False(removal.Success);
        Assert.Equal(
            ManagedFileClassification.CustomizedOwned,
            update.EnvironmentClassification
        );
        Assert.Contains("USER_CUSTOMIZATION=1", File.ReadAllText(EnvironmentPath));
        Assert.Equal(shellBefore, File.ReadAllText(BashProfile));
        Assert.True(File.Exists(Path.Join(StateRoot, "cuda-library-path-environment", "state.json")));
    }

    [Fact]
    public async Task Setup_refuses_environment_symlink_and_preserves_target()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(EnvironmentPath)!);
        var target = Path.Join(_root, "foreign-environment-target");
        await File.WriteAllTextAsync(target, "target bytes\n");
        File.CreateSymbolicLink(EnvironmentPath, target);

        var result = await CreateService().SetUpAsync();

        Assert.False(result.Success);
        Assert.Equal(
            ManagedFileClassification.UnsupportedEntry,
            result.EnvironmentClassification
        );
        Assert.Equal("target bytes\n", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(EnvironmentPath).LinkTarget);
        Assert.False(File.Exists(BashProfile));
    }

    [Theory]
    [InlineData("/bin/bash", ".bashrc", "export LD_LIBRARY_PATH=/opt/cuda-12/lib64:${LD_LIBRARY_PATH:-}")]
    [InlineData("/usr/bin/zsh", ".zshrc", "export LD_LIBRARY_PATH=/opt/cuda-12/lib64:${LD_LIBRARY_PATH:-}")]
    [InlineData("/usr/bin/fish", ".config/fish/config.fish", "set -gx LD_LIBRARY_PATH /opt/cuda-12/lib64 $LD_LIBRARY_PATH")]
    public async Task Sentinel_round_trip_preserves_outside_edits_for_each_shell_flavor(
        string shell,
        string relativeProfile,
        string expectedExport
    )
    {
        var profile = Path.Join(Home, relativeProfile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        const string original = "# user setting\nset-user-option=yes\n";
        await File.WriteAllTextAsync(profile, original);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(profile, Mode0600);
        }

        var service = CreateService(shell);
        var setup = await service.SetUpAsync();
        var installed = File.ReadAllText(profile);
        await File.AppendAllTextAsync(profile, "# outside edit after setup\n");
        var removal = await service.RemoveAsync();

        Assert.True(setup.Success, setup.Detail);
        Assert.True(removal.Success, removal.Detail);
        Assert.Contains("# >>> typewhisper:cuda-library-path", installed);
        Assert.Contains(expectedExport, installed);
        Assert.DoesNotContain("typewhisper:cuda-library-path", File.ReadAllText(profile));
        Assert.Equal(original + "# outside edit after setup\n", File.ReadAllText(profile));
        Assert.False(File.Exists(EnvironmentPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Mode0600, File.GetUnixFileMode(profile));
        }

        Assert.Equal(
            expectedExport,
            CudaLibraryPathSetupService.GetCudaLibraryPathExport(profile, CudaPath)
        );
    }

    [Fact]
    public async Task Setup_retries_compare_and_swap_and_preserves_edit_between_capture_and_commit()
    {
        Directory.CreateDirectory(Home);
        await File.WriteAllTextAsync(BashProfile, "# original\n");
        var injected = false;
        var result = await CreateService(conditionalWrite: ConditionalWrite).SetUpAsync();

        Assert.True(result.Success, result.Detail);
        Assert.True(injected);
        var contents = File.ReadAllText(BashProfile);
        Assert.Contains("# concurrent edit\n", contents);
        Assert.Contains("# >>> typewhisper:cuda-library-path", contents);
        return;

        async Task<bool> ConditionalWrite(
            AtomicFileSnapshot snapshot,
            string updated,
            CancellationToken ct
        )
        {
            // ReSharper disable once InvertIf -- inverting would duplicate the write call below.
            if (!injected)
            {
                injected = true;
                await File.AppendAllTextAsync(snapshot.ResolvedTarget, "# concurrent edit\n", ct);
            }

            return await AtomicFileWriter.WriteIfUnchangedAsync(snapshot, updated, ct);
        }
    }

    [Fact]
    public async Task Shell_profile_symlink_is_followed_without_replacing_link_and_mode_is_preserved()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Home);
        var target = Path.Join(_root, "managed-bashrc");
        await File.WriteAllTextAsync(target, "# dotfile manager content\n");
        File.SetUnixFileMode(target, Mode0600);
        File.CreateSymbolicLink(BashProfile, target);

        var service = CreateService();
        Assert.True((await service.SetUpAsync()).Success);
        Assert.NotNull(new FileInfo(BashProfile).LinkTarget);
        Assert.Contains("typewhisper:cuda-library-path", File.ReadAllText(target));
        Assert.Equal(Mode0600, File.GetUnixFileMode(target));

        Assert.True((await service.RemoveAsync()).Success);
        Assert.NotNull(new FileInfo(BashProfile).LinkTarget);
        Assert.Equal("# dotfile manager content\n", File.ReadAllText(target));
        Assert.Equal(Mode0600, File.GetUnixFileMode(target));
    }

    [Fact]
    public async Task Exact_legacy_environment_and_profile_are_adopted_into_managed_artifacts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EnvironmentPath)!);
        Directory.CreateDirectory(Home);
        const string legacyEnvironment =
            "# TypeWhisper CUDA 12 runtime libraries\n"
            + "LD_LIBRARY_PATH=/opt/cuda-12/lib64:${LD_LIBRARY_PATH:-}\n";
        const string legacyProfile =
            "# user\n\n"
            + "# TypeWhisper CUDA 12 runtime libraries\n"
            + "export LD_LIBRARY_PATH=/opt/cuda-12/lib64:${LD_LIBRARY_PATH:-}\n";
        await File.WriteAllTextAsync(EnvironmentPath, legacyEnvironment);
        await File.WriteAllTextAsync(BashProfile, legacyProfile);

        var result = await CreateService().SetUpAsync();

        Assert.True(result.Success, result.Detail);
        Assert.Equal(
            ManagedFileClassification.StaleOwned,
            result.EnvironmentClassification
        );
        Assert.StartsWith("# Installed by TypeWhisper", File.ReadAllText(EnvironmentPath));
        var profile = File.ReadAllText(BashProfile);
        Assert.Contains("# >>> typewhisper:cuda-library-path", profile);
        Assert.Equal(1, CountOccurrences(profile, "export LD_LIBRARY_PATH="));
    }

    [Fact]
    public async Task Interrupted_environment_publication_recovers_journal_then_finishes_profile()
    {
        var fired = false;
        var interruptedTransaction = new ManagedFileTransaction(
            StateRoot,
            (checkpoint, _) =>
            {
                if (fired || checkpoint != ManagedFileCheckpoint.InstallAfterPublish)
                {
                    return Task.CompletedTask;
                }

                fired = true;
                throw new SimulatedCrashException();
            }
        );
        var interrupted = CreateService(transaction: interruptedTransaction);

        await Assert.ThrowsAsync<SimulatedCrashException>(() => interrupted.SetUpAsync());
        Assert.True(File.Exists(EnvironmentPath));
        Assert.False(File.Exists(BashProfile));
        Assert.True(File.Exists(PendingJournal));

        var recovered = await CreateService().SetUpAsync();

        Assert.True(recovered.Success, recovered.Detail);
        Assert.False(File.Exists(PendingJournal));
        Assert.Contains("typewhisper:cuda-library-path", File.ReadAllText(BashProfile));
        Assert.Equal(
            CudaLibraryPathSetupService.EnvironmentFileContent(CudaPath),
            File.ReadAllText(EnvironmentPath)
        );
    }

    [Fact]
    public async Task Remove_deletes_pre_xdg_environment_file_that_carries_the_marker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyEnvironmentPath)!);
        await File.WriteAllTextAsync(
            LegacyEnvironmentPath,
            CudaLibraryPathSetupService.EnvironmentFileContent(CudaPath)
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(LegacyEnvironmentPath, Mode0600);
        }

        var result = await CreateService().RemoveAsync();

        Assert.True(result.Success, result.Detail);
        Assert.True(result.Changed);
        Assert.Null(result.LegacyEnvironmentNotice);
        Assert.False(File.Exists(LegacyEnvironmentPath));
    }

    [Fact]
    public async Task Remove_leaves_a_foreign_pre_xdg_environment_file_and_reports_it()
    {
        const string foreign = "LD_LIBRARY_PATH=/opt/somebody-else/lib\n";
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyEnvironmentPath)!);
        await File.WriteAllTextAsync(LegacyEnvironmentPath, foreign);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(LegacyEnvironmentPath, Mode0600);
        }

        var result = await CreateService().RemoveAsync();

        Assert.True(result.Success, result.Detail);
        Assert.NotNull(result.LegacyEnvironmentNotice);
        Assert.Contains(LegacyEnvironmentPath, result.LegacyEnvironmentNotice);
        Assert.Equal(foreign, await File.ReadAllTextAsync(LegacyEnvironmentPath));
    }

    [Fact]
    public async Task Setup_sweeps_a_marked_pre_xdg_environment_file_after_publishing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyEnvironmentPath)!);
        await File.WriteAllTextAsync(
            LegacyEnvironmentPath,
            CudaLibraryPathSetupService.EnvironmentFileContent(CudaPath)
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(LegacyEnvironmentPath, Mode0600);
        }

        var result = await CreateService().SetUpAsync();

        Assert.True(result.Success, result.Detail);
        Assert.Null(result.LegacyEnvironmentNotice);
        Assert.False(File.Exists(LegacyEnvironmentPath));
        Assert.Equal(
            CudaLibraryPathSetupService.EnvironmentFileContent(CudaPath),
            await File.ReadAllTextAsync(EnvironmentPath)
        );
    }

    // The default configuration: XDG_CONFIG_HOME unset, so the canonical environment.d
    // path IS the pre-XDG one. The legacy sweep must recognize the collision, or setup
    // would delete the file it had just published.
    [Fact]
    public async Task Setup_does_not_sweep_the_environment_file_it_just_published_at_the_legacy_path()
    {
        var collidingConfigHome = Path.Join(Home, ".config");

        var result = await CreateService(configHome: collidingConfigHome).SetUpAsync();

        Assert.True(result.Success, result.Detail);
        Assert.Null(result.LegacyEnvironmentNotice);
        Assert.True(File.Exists(LegacyEnvironmentPath));
        Assert.Equal(
            CudaLibraryPathSetupService.EnvironmentFileContent(CudaPath),
            await File.ReadAllTextAsync(LegacyEnvironmentPath)
        );
    }

    // A profile that still carries the pre-manifest pair below our sentinel block: the
    // block repeats that same comment/export shape, so a sweep that matched inside it
    // would strip its body and leave the stray pair exporting the path forever.
    [Fact]
    public async Task Setup_strips_a_stray_pre_manifest_fragment_below_an_existing_block()
    {
        var service = CreateService();
        Assert.True((await service.SetUpAsync()).Success);
        await File.AppendAllTextAsync(
            BashProfile,
            "\n# TypeWhisper CUDA 12 runtime libraries\n"
            + "export LD_LIBRARY_PATH=/opt/cuda-12/lib64:${LD_LIBRARY_PATH:-}\n"
        );

        var result = await service.SetUpAsync();

        Assert.True(result.Success, result.Detail);
        var profile = await File.ReadAllTextAsync(BashProfile);
        Assert.Equal(
            1,
            CountOccurrences(profile, "# TypeWhisper CUDA 12 runtime libraries")
        );
        Assert.Equal(
            1,
            CountOccurrences(profile, "export LD_LIBRARY_PATH=/opt/cuda-12/lib64")
        );
    }

    // Upgrading from the pre-manifest release and going straight to Remove: the shell
    // profile still carries only the old unsentinelized pair. Leaving it would keep
    // LD_LIBRARY_PATH exported after the environment.d file is gone.
    [Fact]
    public async Task RemoveAsync_strips_a_pre_manifest_profile_fragment_without_prior_setup()
    {
        Directory.CreateDirectory(Home);
        await File.WriteAllTextAsync(
            BashProfile,
            "# user\n\n"
            + "# TypeWhisper CUDA 12 runtime libraries\n"
            + "export LD_LIBRARY_PATH=/opt/cuda-12/lib64:${LD_LIBRARY_PATH:-}\n"
        );
        var service = CreateService();

        Assert.True(service.HasInstalledChanges());

        var result = await service.RemoveAsync();

        Assert.True(result.Success, result.Detail);
        Assert.True(result.Changed);
        Assert.Equal("# user\n", await File.ReadAllTextAsync(BashProfile));
        Assert.False(service.HasInstalledChanges());
    }

    private string Home => Path.Join(_root, "home");
    private string ConfigHome => Path.Join(_root, "config");
    private string StateRoot => Path.Join(_root, "state");
    private string BashProfile => Path.Join(Home, ".bashrc");
    private string EnvironmentPath =>
        Path.Join(ConfigHome, "environment.d", "typewhisper-cuda.conf");
    private string LegacyEnvironmentPath =>
        Path.Join(Home, ".config", "environment.d", "typewhisper-cuda.conf");
    private string PendingJournal =>
        Path.Join(StateRoot, "cuda-library-path-environment", "pending.json");

    private CudaLibraryPathSetupService CreateService(
        string shell = "/bin/bash",
        ManagedFileTransaction? transaction = null,
        Func<AtomicFileSnapshot, string, CancellationToken, Task<bool>>? conditionalWrite = null,
        Func<string>? shellProvider = null,
        string? configHome = null
    )
    {
        return new CudaLibraryPathSetupService(
            transaction ?? new ManagedFileTransaction(StateRoot),
            () => Home,
            () => configHome ?? ConfigHome,
            shellProvider ?? (() => shell),
            () => CudaPath,
            conditionalWrite
        );
    }

    private static int CountOccurrences(string value, string search)
    {
        return value.Split(search).Length - 1;
    }

    private sealed class SimulatedCrashException : Exception;
}
