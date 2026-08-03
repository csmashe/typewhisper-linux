// ReSharper disable MethodHasAsyncOverload -- synchronous File.ReadAll* is deliberate in these test assertions.
using System.Diagnostics;
using System.Runtime.Versioning;
using TypeWhisper.Linux.Services.ManagedArtifacts;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class PrivilegedManagedFileTransactionTests : IDisposable
{
    private readonly string _root = TestPaths.CreateTempDirectory("privileged-managed-files");

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_root);
        }
        catch
        {
            // Best-effort cleanup for killed shell fixtures.
        }
    }

    [Fact]
    public async Task Install_and_remove_record_exact_images_and_explicit_modes()
    {
        var spec = Spec("one", "managed one\n");

        var install = await RunAsync(BuildInstall([spec]));

        Assert.Equal(0, install.ExitCode);
        Assert.Equal("managed one\n", File.ReadAllText(spec.DestinationPath));
        Assert.Equal("managed one\n", File.ReadAllText(StatePath("one", "current")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("644", Mode(spec.DestinationPath));
            Assert.Equal("700", Mode(Path.Join(StateRoot, "one")));
            Assert.Equal("600", Mode(StatePath("one", "current")));
        }

        var removal = await RunAsync(BuildRemove([spec]));

        Assert.Equal(0, removal.ExitCode);
        Assert.False(File.Exists(spec.DestinationPath));
        Assert.False(File.Exists(StatePath("one", "current")));
    }

    [Fact]
    public async Task Foreign_customized_and_symlink_destinations_are_preserved()
    {
        var foreignSpec = Spec("foreign", "managed\n");
        Directory.CreateDirectory(Path.GetDirectoryName(foreignSpec.DestinationPath)!);
        await File.WriteAllTextAsync(foreignSpec.DestinationPath, "foreign\n");

        var foreign = await RunAsync(BuildInstall([foreignSpec]));

        Assert.Equal(foreignSpec.ConflictExitCode, foreign.ExitCode);
        Assert.Contains(foreignSpec.ConflictToken, foreign.StandardError);
        Assert.Equal("foreign\n", File.ReadAllText(foreignSpec.DestinationPath));

        var customizedSpec = Spec("customized", "managed\n");
        Assert.Equal(0, (await RunAsync(BuildInstall([customizedSpec]))).ExitCode);
        await File.WriteAllTextAsync(customizedSpec.DestinationPath, "managed\nuser edit\n");

        var customizedInstall = await RunAsync(BuildInstall([customizedSpec]));
        var customizedRemove = await RunAsync(BuildRemove([customizedSpec]));

        Assert.Equal(customizedSpec.ConflictExitCode, customizedInstall.ExitCode);
        Assert.Equal(customizedSpec.ConflictExitCode, customizedRemove.ExitCode);
        Assert.Equal(
            "managed\nuser edit\n",
            File.ReadAllText(customizedSpec.DestinationPath)
        );
        Assert.True(File.Exists(StatePath("customized", "current")));

        if (!OperatingSystem.IsWindows())
        {
            var symlinkSpec = Spec("symlink", "managed\n");
            Directory.CreateDirectory(Path.GetDirectoryName(symlinkSpec.DestinationPath)!);
            var target = Path.Join(_root, "symlink-target");
            await File.WriteAllTextAsync(target, "target\n");
            File.CreateSymbolicLink(symlinkSpec.DestinationPath, target);

            var symlink = await RunAsync(BuildInstall([symlinkSpec]));

            Assert.Equal(symlinkSpec.SymlinkExitCode, symlink.ExitCode);
            Assert.Equal("target\n", File.ReadAllText(target));
            Assert.NotNull(new FileInfo(symlinkSpec.DestinationPath).LinkTarget);
        }
    }

    [Fact]
    public async Task Kill_after_journal_recovers_old_state_and_cleans_recorded_stage()
    {
        var spec = Spec("journal-kill", "new image\n");
        var killedScript = PrivilegedManagedFileTransaction.BuildInstallScript(
            StateRoot,
            [spec],
            afterCommitShell: ":\n",
            new PrivilegedManagedFileTestHooks(AfterJournalsShell: "kill -KILL $$")
        );

        var killed = await RunAsync(killedScript);
        Assert.NotEqual(0, killed.ExitCode);
        Assert.False(File.Exists(spec.DestinationPath));
        Assert.True(File.Exists(StatePath("journal-kill", "pending.operation")));

        var recovered = await RunAsync(BuildInstall([spec]));

        Assert.True(
            recovered.ExitCode == 0,
            $"Recovery failed with {recovered.ExitCode}: {recovered.StandardError}{recovered.StandardOutput}"
        );
        Assert.Equal("new image\n", File.ReadAllText(spec.DestinationPath));
        Assert.False(File.Exists(StatePath("journal-kill", "pending.operation")));
        Assert.Empty(
            Directory.EnumerateFiles(
                Path.GetDirectoryName(spec.DestinationPath)!,
                $"{Path.GetFileName(spec.DestinationPath)}.typewhisper.*"
            )
        );
    }

    [Fact]
    public async Task Bundle_kill_after_first_publish_recovers_mixed_state()
    {
        var first = Spec("bundle-first", "first new\n");
        var second = Spec("bundle-second", "second new\n");
        Directory.CreateDirectory(Path.GetDirectoryName(second.DestinationPath)!);
        await File.WriteAllTextAsync(second.DestinationPath, "second old\n");
        // Exact legacy adoption is intentionally limited to desired bytes, so record
        // the old second publication before exercising a bundle update.
        var oldSecond = second with { DesiredContent = "second old\n" };
        Assert.Equal(0, (await RunAsync(BuildInstall([oldSecond]))).ExitCode);

        var killedScript = PrivilegedManagedFileTransaction.BuildInstallScript(
            StateRoot,
            [first, second],
            afterCommitShell: ":\n",
            new PrivilegedManagedFileTestHooks(
                AfterPublishShell: new Dictionary<int, string> { [0] = "kill -KILL $$" }
            )
        );

        var killed = await RunAsync(killedScript);
        Assert.NotEqual(0, killed.ExitCode);
        Assert.Equal("first new\n", File.ReadAllText(first.DestinationPath));
        Assert.Equal("second old\n", File.ReadAllText(second.DestinationPath));

        var recovered = await RunAsync(BuildInstall([first, second]));

        Assert.True(
            recovered.ExitCode == 0,
            $"Recovery failed with {recovered.ExitCode}: {recovered.StandardError}{recovered.StandardOutput}"
        );
        Assert.Equal("first new\n", File.ReadAllText(first.DestinationPath));
        Assert.Equal("second new\n", File.ReadAllText(second.DestinationPath));
        Assert.False(File.Exists(StatePath("bundle-first", "pending.operation")));
        Assert.False(File.Exists(StatePath("bundle-second", "pending.operation")));
    }

    [Fact]
    public async Task Interrupted_remove_finishes_when_exact_publication_is_already_absent()
    {
        var spec = Spec("remove-kill", "managed\n");
        Assert.Equal(0, (await RunAsync(BuildInstall([spec]))).ExitCode);
        var killedScript = PrivilegedManagedFileTransaction.BuildRemoveScript(
            StateRoot,
            [spec],
            afterCommitShell: ":\n",
            new PrivilegedManagedFileTestHooks(
                AfterPublishShell: new Dictionary<int, string> { [0] = "kill -KILL $$" }
            )
        );

        var killed = await RunAsync(killedScript);
        Assert.NotEqual(0, killed.ExitCode);
        Assert.False(File.Exists(spec.DestinationPath));

        var recovered = await RunAsync(BuildRemove([spec]));

        Assert.Equal(0, recovered.ExitCode);
        Assert.False(File.Exists(StatePath("remove-kill", "current")));
        Assert.False(File.Exists(StatePath("remove-kill", "pending.operation")));
    }

    [Fact]
    public async Task Recovery_conflict_preserves_external_bytes_and_pending_images()
    {
        var spec = Spec("recovery-conflict", "new\n");
        var editCommand = $"printf '%s\\n' external-edit > {ShellQuote(spec.DestinationPath)}; kill -KILL $$";
        var interrupted = PrivilegedManagedFileTransaction.BuildInstallScript(
            StateRoot,
            [spec],
            afterCommitShell: ":\n",
            new PrivilegedManagedFileTestHooks(AfterJournalsShell: editCommand)
        );
        Assert.NotEqual(0, (await RunAsync(interrupted)).ExitCode);

        var recovery = await RunAsync(BuildInstall([spec]));

        Assert.Equal(spec.ConflictExitCode, recovery.ExitCode);
        Assert.Equal("external-edit\n", File.ReadAllText(spec.DestinationPath));
        Assert.True(File.Exists(StatePath("recovery-conflict", "pending.operation")));
        Assert.True(File.Exists(StatePath("recovery-conflict", "pending.new")));
    }

    [Fact]
    public async Task Concurrent_root_transactions_serialize_through_flock()
    {
        var gate = Path.Join(_root, "first-holds-lock");
        var release = Path.Join(_root, "release-first");
        var first = Spec("serialized-root", "first\n");
        var second = first with { DesiredContent = "second\n" };
        var firstScript = PrivilegedManagedFileTransaction.BuildInstallScript(
            StateRoot,
            [first],
            afterCommitShell: ":\n",
            new PrivilegedManagedFileTestHooks(
                AfterJournalsShell:
                    $"touch {ShellQuote(gate)}; while [ ! -e {ShellQuote(release)} ]; do sleep 0.01; done"
            )
        );

        var firstTask = RunAsync(firstScript);
        await WaitForFileAsync(gate);
        var secondTask = RunAsync(BuildInstall([second]));
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        await File.WriteAllTextAsync(release, string.Empty);
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal("second\n", File.ReadAllText(first.DestinationPath));
    }

    [Fact]
    public async Task Missing_flock_fails_closed_with_clear_token()
    {
        var spec = Spec("no-flock", "managed\n");
        var result = await RunAsync(BuildInstall([spec]), includeSystemPath: false);

        Assert.Equal(PrivilegedManagedFileTransaction.FlockUnavailableExitCode, result.ExitCode);
        Assert.Contains(PrivilegedManagedFileTransaction.FlockUnavailableToken, result.StandardError);
        Assert.False(File.Exists(spec.DestinationPath));
    }

    private string StateRoot => Path.Join(_root, "state");

    private PrivilegedManagedFileSpec Spec(string artifactId, string contents)
    {
        return new PrivilegedManagedFileSpec(
            artifactId,
            Path.Join(_root, "destinations", $"{artifactId}.conf"),
            contents,
            73,
            $"CONFLICT_{artifactId}",
            74,
            $"SYMLINK_{artifactId}"
        );
    }

    private string BuildInstall(IReadOnlyList<PrivilegedManagedFileSpec> specs)
    {
        return PrivilegedManagedFileTransaction.BuildInstallScript(StateRoot, specs, ":\n");
    }

    private string BuildRemove(IReadOnlyList<PrivilegedManagedFileSpec> specs)
    {
        return PrivilegedManagedFileTransaction.BuildRemoveScript(StateRoot, specs, ":\n");
    }

    private string StatePath(string artifactId, string name)
    {
        return Path.Join(StateRoot, artifactId, name);
    }

    private async Task<ScriptResult> RunAsync(
        string script,
        bool includeSystemPath = true
    )
    {
        Directory.CreateDirectory(Path.Join(_root, "destinations"));
        var shim = Path.Join(_root, "shim");
        Directory.CreateDirectory(shim);
        var chown = Path.Join(shim, "chown");
        if (!File.Exists(chown))
        {
            await File.WriteAllTextAsync(chown, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(
                chown,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment =
            {
                ["PATH"] = includeSystemPath
                    ? $"{shim}{Path.PathSeparator}/usr/bin{Path.PathSeparator}/bin"
                    : shim,
            },
        };
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private static string Mode(string path)
    {
        var mode = File.GetUnixFileMode(path);
        return Convert.ToString((int)mode, 8).PadLeft(3, '0')[^3..];
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record ScriptResult(int ExitCode, string StandardOutput, string StandardError);
}
