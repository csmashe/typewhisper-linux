using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CompositorShortcutWriterConcurrencyTests : IDisposable
{
    private const string ShortcutId = "typewhisper.dictation.toggle";
    private readonly string _binDirectory;
    private readonly string _liveInvocationLog;
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string? _originalXdgConfigHome = Environment.GetEnvironmentVariable(
        "XDG_CONFIG_HOME"
    );
    private readonly string _tempDirectory = TestPaths.CreateTempDirectory(
        "compositor-shortcut-concurrency"
    );

    public CompositorShortcutWriterConcurrencyTests()
    {
        _binDirectory = Path.Join(_tempDirectory, "bin");
        _liveInvocationLog = Path.Join(_tempDirectory, "live-invocations.log");
        Directory.CreateDirectory(_binDirectory);
        CreateLiveCommand("hyprctl");
        CreateLiveCommand("swaymsg");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempDirectory);
        Environment.SetEnvironmentVariable("PATH", _binDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdgConfigHome);
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            TestPaths.DeleteDirectory(_tempDirectory);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public async Task HyprlandWriteAsync_RetriesUpdateAndPreservesConcurrentOutsideLine()
    {
        const string concurrentLine = "# user added this during the Hyprland update";
        WriteConfig(
            HyprlandConfigPath,
            ManagedConfig("monitor = preferred", "old hyprland managed bind")
        );
        var attempts = 0;
        var writer = new HyprlandShortcutWriter(EditBeforeFirstCommit);

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        var contents = await File.ReadAllTextAsync(HyprlandConfigPath);
        Assert.True(result.Success);
        Assert.Equal(2, attempts);
        Assert.Contains("monitor = preferred", contents);
        Assert.Contains(concurrentLine, contents);
        Assert.Contains("typewhisper record toggle", contents);
        Assert.DoesNotContain("old hyprland managed bind", contents);
        Assert.Equal(1, CountOccurrences(contents, SentinelBlock.OpenSentinel));
        Assert.Equal(["hyprctl reload"], await File.ReadAllLinesAsync(_liveInvocationLog));
        return;

        async Task<bool> EditBeforeFirstCommit(
            AtomicFileSnapshot snapshot,
            string updated,
            CancellationToken ct
        )
        {
            Assert.False(File.Exists(_liveInvocationLog));
            attempts++;
            if (attempts == 1)
            {
                await File.AppendAllTextAsync(snapshot.ResolvedTarget, concurrentLine + "\n", ct);
            }

            return await AtomicFileWriter.WriteIfUnchangedAsync(snapshot, updated, ct);
        }
    }

    [Fact]
    public async Task HyprlandRemoveAsync_RetriesAndPreservesConcurrentOutsideLine()
    {
        const string concurrentLine = "# user added this during the Hyprland removal";
        WriteConfig(
            HyprlandConfigPath,
            ManagedConfig("monitor = preferred", "old hyprland managed bind")
        );
        var attempts = 0;
        var writer = new HyprlandShortcutWriter(EditBeforeFirstCommit);

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        var contents = await File.ReadAllTextAsync(HyprlandConfigPath);
        Assert.True(result.Success);
        Assert.Null(result.Warning);
        Assert.Equal(2, attempts);
        Assert.Contains("monitor = preferred", contents);
        Assert.Contains(concurrentLine, contents);
        Assert.DoesNotContain(SentinelBlock.OpenSentinel, contents);
        Assert.DoesNotContain("old hyprland managed bind", contents);
        Assert.Equal(["hyprctl reload"], await File.ReadAllLinesAsync(_liveInvocationLog));
        return;

        async Task<bool> EditBeforeFirstCommit(
            AtomicFileSnapshot snapshot,
            string updated,
            CancellationToken ct
        )
        {
            Assert.False(File.Exists(_liveInvocationLog));
            attempts++;
            if (attempts == 1)
            {
                await File.AppendAllTextAsync(snapshot.ResolvedTarget, concurrentLine + "\n", ct);
            }

            return await AtomicFileWriter.WriteIfUnchangedAsync(snapshot, updated, ct);
        }
    }

    [Fact]
    public async Task SwayWriteAsync_RetriesUpdateAndPreservesConcurrentOutsideLine()
    {
        const string concurrentLine = "# user added this during the Sway update";
        WriteConfig(SwayConfigPath, ManagedConfig("set $mod Mod4", "old sway managed bind"));
        var attempts = 0;
        var writer = new SwayShortcutWriter(EditBeforeFirstCommit);

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        var contents = await File.ReadAllTextAsync(SwayConfigPath);
        Assert.True(result.Success);
        Assert.Equal(2, attempts);
        Assert.Contains("set $mod Mod4", contents);
        Assert.Contains(concurrentLine, contents);
        Assert.Contains("typewhisper record toggle", contents);
        Assert.DoesNotContain("old sway managed bind", contents);
        Assert.Equal(1, CountOccurrences(contents, SentinelBlock.OpenSentinel));
        return;

        async Task<bool> EditBeforeFirstCommit(
            AtomicFileSnapshot snapshot,
            string updated,
            CancellationToken ct
        )
        {
            attempts++;
            if (attempts == 1)
            {
                await File.AppendAllTextAsync(snapshot.ResolvedTarget, concurrentLine + "\n", ct);
            }

            return await AtomicFileWriter.WriteIfUnchangedAsync(snapshot, updated, ct);
        }
    }

    [Fact]
    public async Task SwayRemoveAsync_RetriesAndPreservesConcurrentOutsideLine()
    {
        const string concurrentLine = "# user added this during the Sway removal";
        WriteConfig(SwayConfigPath, ManagedConfig("set $mod Mod4", "old sway managed bind"));
        var attempts = 0;
        var writer = new SwayShortcutWriter(EditBeforeFirstCommit);

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        var contents = await File.ReadAllTextAsync(SwayConfigPath);
        Assert.True(result.Success);
        Assert.Equal(2, attempts);
        Assert.Contains("set $mod Mod4", contents);
        Assert.Contains(concurrentLine, contents);
        Assert.DoesNotContain(SentinelBlock.OpenSentinel, contents);
        Assert.DoesNotContain("old sway managed bind", contents);
        return;

        async Task<bool> EditBeforeFirstCommit(
            AtomicFileSnapshot snapshot,
            string updated,
            CancellationToken ct
        )
        {
            attempts++;
            if (attempts == 1)
            {
                await File.AppendAllTextAsync(snapshot.ResolvedTarget, concurrentLine + "\n", ct);
            }

            return await AtomicFileWriter.WriteIfUnchangedAsync(snapshot, updated, ct);
        }
    }

    [Theory]
    [InlineData("hyprland")]
    [InlineData("sway")]
    public async Task WriteAsync_RepeatedConflictsExhaustBoundWithoutApplyingLive(
        string compositor
    )
    {
        var path = compositor == "hyprland" ? HyprlandConfigPath : SwayConfigPath;
        WriteConfig(path, "# original user config\n");
        var attempts = 0;
        IDeShortcutWriter writer = compositor == "hyprland"
            ? new HyprlandShortcutWriter(EditBeforeEveryCommit)
            : new SwayShortcutWriter(EditBeforeEveryCommit);

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        var contents = await File.ReadAllTextAsync(path);
        Assert.False(result.Success);
        Assert.Contains("Please retry", result.UserMessage);
        Assert.Equal(3, attempts);
        Assert.Contains("# original user config", contents);
        Assert.Contains("# concurrent user edit 3", contents);
        Assert.DoesNotContain(SentinelBlock.OpenSentinel, contents);
        Assert.False(File.Exists(_liveInvocationLog));
        return;

        async Task<bool> EditBeforeEveryCommit(
            AtomicFileSnapshot snapshot,
            string updated,
            CancellationToken ct
        )
        {
            attempts++;
            await File.AppendAllTextAsync(
                snapshot.ResolvedTarget,
                $"# concurrent user edit {attempts}\n",
                ct
            );
            return await AtomicFileWriter.WriteIfUnchangedAsync(snapshot, updated, ct);
        }
    }

    [Theory]
    [InlineData("hyprland")]
    [InlineData("sway")]
    public async Task ManagedPresence_DistinguishesAbsentCurrentStaleAndUnbalanced(
        string compositor
    )
    {
        var path = compositor == "hyprland" ? HyprlandConfigPath : SwayConfigPath;
        IDeShortcutWriter writer = compositor == "hyprland"
            ? new HyprlandShortcutWriter()
            : new SwayShortcutWriter();
        var installed = CreateSpec();
        var changed = installed with { Trigger = "Alt+F8" };

        Assert.False(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, CancellationToken.None)
        );
        Assert.False(await writer.IsInstalledAsync(installed, CancellationToken.None));

        var write = await writer.WriteAsync(installed, CancellationToken.None);

        Assert.True(write.Success);
        Assert.True(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, CancellationToken.None)
        );
        Assert.True(await writer.IsInstalledAsync(installed, CancellationToken.None));
        Assert.False(await writer.IsInstalledAsync(changed, CancellationToken.None));
        Assert.True(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, CancellationToken.None)
        );

        WriteConfig(path, SentinelBlock.OpenSentinel + "\nstale bind\n");

        Assert.False(
            await writer.IsManagedShortcutPresentAsync(ShortcutId, CancellationToken.None)
        );
    }

    [Fact]
    public async Task HyprlandWriteAndRemoval_ReloadFailureWarnsAfterCommittedChanges()
    {
        CreateLiveCommand("hyprctl", exitCode: 1);
        var writer = new HyprlandShortcutWriter();

        var write = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.True(write.Success);
        Assert.NotNull(write.Warning);
        Assert.Contains("hyprctl reload", write.Warning);

        var removal = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(removal.Success);
        Assert.NotNull(removal.Warning);
        Assert.Contains("hyprctl reload", removal.Warning);
        Assert.Equal(
            ["hyprctl reload", "hyprctl reload"],
            await File.ReadAllLinesAsync(_liveInvocationLog)
        );
    }

    private string HyprlandConfigPath => Path.Join(_tempDirectory, "hypr", "hyprland.conf");

    private string SwayConfigPath => Path.Join(_tempDirectory, "sway", "config");

    private void CreateLiveCommand(string name, int exitCode = 0)
    {
        var path = Path.Join(_binDirectory, name);
        File.WriteAllText(
            path,
            $"#!/bin/sh\nprintf '%s %s\\n' {name} \"$*\" >> \"{_liveInvocationLog}\"\nexit {exitCode}\n"
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
    }

    private static DeShortcutSpec CreateSpec()
    {
        return new DeShortcutSpec(
            ShortcutId,
            "TypeWhisper Dictation",
            "Ctrl+Shift+Space",
            "typewhisper record toggle",
            "typewhisper record stop",
            null,
            null
        );
    }

    private static string ManagedConfig(string outsideLine, string managedLine)
    {
        return outsideLine
               + "\n"
               + SentinelBlock.OpenSentinel
               + "\n"
               + managedLine
               + "\n"
               + SentinelBlock.CloseSentinel
               + "\n";
    }

    private static void WriteConfig(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static int CountOccurrences(string value, string needle)
    {
        return value.Split(needle).Length - 1;
    }
}
