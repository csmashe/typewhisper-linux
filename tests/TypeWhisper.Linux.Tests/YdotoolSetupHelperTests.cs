using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Insertion;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Covers the pure / <c>internal static</c> surface of
/// <see cref="YdotoolSetupHelper"/>. The process- and syscall-coupled
/// paths (pkexec, systemctl, libc <c>access</c>) can't be meaningfully
/// mocked — the helper calls <c>Process.Start</c> and P/Invoke directly —
/// so they're verified manually instead.
/// </summary>
public sealed class YdotoolSetupHelperTests
{
    [Fact]
    public void UserUnitFilePath_honors_XDG_CONFIG_HOME_when_set()
    {
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var custom = Path.Combine(Path.GetTempPath(), $"tw-xdg-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", custom);

            var path = YdotoolSetupHelper.UserUnitFilePath();

            Assert.Equal(
                Path.Combine(custom, "systemd", "user", "ydotoold.service"),
                path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
        }
    }

    [Fact]
    public void UserUnitFilePath_falls_back_to_dot_config_when_XDG_unset()
    {
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

            var path = YdotoolSetupHelper.UserUnitFilePath();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(
                Path.Combine(home, ".config", "systemd", "user", "ydotoold.service"),
                path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
        }
    }

    [Fact]
    public void BuildUserUnitContent_first_line_carries_ownership_marker()
    {
        var content = YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold");

        var firstLine = content.Split('\n')[0];
        Assert.StartsWith("# Installed by TypeWhisper", firstLine);
    }

    [Fact]
    public void BuildUserUnitContent_embeds_exact_ExecStart_path()
    {
        const string path = "/opt/custom/bin/ydotoold";

        var content = YdotoolSetupHelper.BuildUserUnitContent(path);

        Assert.Contains($"ExecStart={path}", content);
        Assert.Contains("WantedBy=default.target", content);
        Assert.Contains("Restart=on-failure", content);
    }

    [Fact]
    public void ResolveBinaryPath_finds_binary_on_PATH()
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        var dir = Path.Combine(Path.GetTempPath(), $"tw-path-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var fake = Path.Combine(dir, "fake-binary");
            File.WriteAllText(fake, "#!/bin/sh\n");

            Environment.SetEnvironmentVariable("PATH", dir);

            Assert.Equal(fake, YdotoolSetupHelper.ResolveBinaryPath("fake-binary"));
            Assert.Null(YdotoolSetupHelper.ResolveBinaryPath("definitely-not-here"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsFileOwnedByTypeWhisper_detects_marker()
    {
        var withMarker = Path.GetTempFileName();
        var withoutMarker = Path.GetTempFileName();
        try
        {
            File.WriteAllText(withMarker, "# Installed by TypeWhisper\nsome content\n");
            File.WriteAllText(withoutMarker, "# Some other tool wrote this\n");

            Assert.True(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(withMarker));
            Assert.False(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(withoutMarker));
            Assert.False(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(
                Path.Combine(Path.GetTempPath(), $"tw-missing-{Guid.NewGuid():N}")));
        }
        finally
        {
            try { File.Delete(withMarker); } catch { }
            try { File.Delete(withoutMarker); } catch { }
        }
    }

    [Fact]
    public void BuildUserUnitContent_round_trips_through_ownership_check()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold"));

            Assert.True(YdotoolSetupHelper.IsFileOwnedByTypeWhisper(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // --- RemoveAsync ownership gating -------------------------------------
    // RemoveAsync runs `systemctl`, so it's only testable through the
    // IProcessRunner seam. The regression these guard: SetUpAsync respects a
    // pre-existing foreign ydotoold user unit but also `enable --now`s it, so
    // an unconditional `disable --now` on remove would kill a service the
    // user relies on, persistently, past logout.

    [Fact]
    public async Task RemoveAsync_leaves_a_foreign_ydotoold_user_unit_enabled()
    {
        using var env = new TempEnvironment();
        // A ydotoold user unit the user (or a distro/AUR package) wrote —
        // no TypeWhisper ownership marker.
        env.WriteUserUnit("# Some other tool's ydotoold unit\n[Service]\nExecStart=/usr/bin/ydotoold\n");
        // systemctl on PATH so the disable isn't skipped for the *wrong*
        // reason — this proves the ownership gate, not a missing binary.
        env.PutFakeBinaryOnPath("systemctl");

        var runner = new FakeProcessRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain(runner.Invocations, i =>
            i.FileName == "systemctl" && i.Args.Contains("disable"));
        // The foreign unit file is left in place, untouched.
        Assert.True(File.Exists(YdotoolSetupHelper.UserUnitFilePath()));
    }

    [Fact]
    public async Task RemoveAsync_disables_and_deletes_a_TypeWhisper_owned_user_unit()
    {
        using var env = new TempEnvironment();
        env.WriteUserUnit(YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold"));
        env.PutFakeBinaryOnPath("systemctl");

        var runner = new FakeProcessRunner();
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(runner.Invocations, i =>
            i.FileName == "systemctl"
            && i.Args.Contains("disable")
            && i.Args.Contains("ydotoold.service"));
        Assert.False(File.Exists(YdotoolSetupHelper.UserUnitFilePath()));
    }

    [Fact]
    public async Task RemoveAsync_keeps_the_unit_file_when_disable_fails()
    {
        using var env = new TempEnvironment();
        env.WriteUserUnit(YdotoolSetupHelper.BuildUserUnitContent("/usr/bin/ydotoold"));
        env.PutFakeBinaryOnPath("systemctl");

        var runner = new FakeProcessRunner();
        runner.FailWhen(
            (file, args) => file == "systemctl" && args.Contains("disable"),
            stderr: "Failed to disable unit: Connection refused");
        var helper = new YdotoolSetupHelper(new SystemCommandAvailabilityService(), runner);

        var result = await helper.RemoveAsync(CancellationToken.None);

        // Fail closed: removal reports failure and leaves the unit file in
        // place so the user can retry — deleting it now would risk a
        // dangling enablement symlink.
        Assert.False(result.Success);
        Assert.True(File.Exists(YdotoolSetupHelper.UserUnitFilePath()));
    }

    /// <summary>
    /// Points XDG_CONFIG_HOME and PATH at throwaway temp dirs for one test,
    /// then restores them. PATH is deliberately restricted to the temp dir so
    /// the test can never reach the real systemctl/pkexec — process execution
    /// goes through the injected <see cref="RecordingProcessRunner"/>, while
    /// <c>DesktopDetector.BinaryExists</c> still resolves whatever fake
    /// binaries the test places there.
    /// </summary>
    private sealed class TempEnvironment : IDisposable
    {
        private readonly string? _originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string _configHome = Path.Combine(Path.GetTempPath(), $"tw-cfg-{Guid.NewGuid():N}");
        private readonly string _pathDir = Path.Combine(Path.GetTempPath(), $"tw-path-{Guid.NewGuid():N}");

        public TempEnvironment()
        {
            Directory.CreateDirectory(_pathDir);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configHome);
            Environment.SetEnvironmentVariable("PATH", _pathDir);
        }

        public void WriteUserUnit(string content)
        {
            var path = YdotoolSetupHelper.UserUnitFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void PutFakeBinaryOnPath(string name)
            => File.WriteAllText(Path.Combine(_pathDir, name), "#!/bin/sh\n");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdg);
            Environment.SetEnvironmentVariable("PATH", _originalPath);
            try { Directory.Delete(_configHome, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(_pathDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
