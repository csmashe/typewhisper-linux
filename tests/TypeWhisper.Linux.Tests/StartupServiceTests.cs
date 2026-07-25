// ReSharper disable MethodHasAsyncOverload -- synchronous file operations keep the assertions direct.
using System.Diagnostics;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class StartupServiceTests : IDisposable
{
    private const string ManagedLine = "X-TypeWhisper-Managed=true";
    private readonly string? _originalXdgConfigHome = Environment.GetEnvironmentVariable(
        "XDG_CONFIG_HOME"
    );
    private readonly string _tempDir = TestPaths.CreateTempDirectory("startup-service");

    public StartupServiceTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdgConfigHome);
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
    public void BuildDesktopFile_pins_canonical_and_legacy_bytes()
    {
        const string execPath = "/opt/typewhisper/TypeWhisper.Linux";
        const string iconPath = "/opt/typewhisper/Resources/typewhisper-128.png";
        const string expectedLegacy =
            "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=TypeWhisper\n"
            + "GenericName=Voice-to-text dictation\n"
            + "Exec=\"/opt/typewhisper/TypeWhisper.Linux\" --minimized\n"
            + "Icon=/opt/typewhisper/Resources/typewhisper-128.png\n"
            + "Terminal=false\n"
            + "Categories=Utility;Accessibility;\n"
            + "X-GNOME-Autostart-enabled=true";
        const string expectedCanonical = expectedLegacy + "\n" + ManagedLine;

        Assert.Equal(
            expectedCanonical,
            StartupService.BuildDesktopFile(execPath, iconPath, includeManagedMarker: true)
        );
        Assert.Equal(
            expectedLegacy,
            StartupService.BuildDesktopFile(execPath, iconPath, includeManagedMarker: false)
        );
        Assert.False(expectedCanonical.EndsWith('\n'));
    }

    [Fact]
    public void Enable_installs_a_marker_bearing_entry_when_missing()
    {
        var result = StartupService.Enable();

        Assert.True(result.Success);
        Assert.True(result.IsEnabled);
        Assert.StartsWith(
            _tempDir + Path.DirectorySeparatorChar,
            TargetPath,
            StringComparison.Ordinal
        );
        Assert.True(File.Exists(TargetPath));
        Assert.Equal(
            CurrentDesktopContent(includeManagedMarker: true),
            File.ReadAllText(TargetPath)
        );
        Assert.True(StartupService.IsEnabled);
        Assert.Equal(Loc.Instance["General.AutostartHint"], result.StatusText);
        Assert.False(string.IsNullOrWhiteSpace(result.StatusText));
    }

    [Fact]
    public void Enable_refuses_and_preserves_a_foreign_entry()
    {
        const string foreignContent =
            "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=Distro Helper\n"
            + "Exec=/usr/libexec/distro-helper --session\n"
            + "Hidden=true";
        WriteTarget(foreignContent);
        var originalBytes = File.ReadAllBytes(TargetPath);

        var result = StartupService.Enable();

        Assert.False(result.Success);
        Assert.False(result.IsEnabled);
        Assert.True(File.Exists(TargetPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(TargetPath));
        Assert.False(StartupService.IsEnabled);
        AssertRefusalStatus(result);
    }

    [Fact]
    public void Disable_refuses_and_preserves_a_foreign_entry()
    {
        const string foreignContent =
            "[Desktop Entry]\nName=Session Agent\nExec=/usr/bin/session-agent\nHidden=true";
        WriteTarget(foreignContent);
        var originalBytes = File.ReadAllBytes(TargetPath);

        var result = StartupService.Disable();

        Assert.False(result.Success);
        Assert.False(result.IsEnabled);
        Assert.True(File.Exists(TargetPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(TargetPath));
        Assert.False(StartupService.IsEnabled);
        AssertRefusalStatus(result);
    }

    [Fact]
    public void Customized_legacy_entry_is_unowned_and_preserved_by_both_operations()
    {
        var legacyContent = CurrentDesktopContent(includeManagedMarker: false);
        var customizedContents = new[]
        {
            legacyContent + "\nHidden=true",
            legacyContent.Replace(
                "Name=TypeWhisper",
                "name=typewhisper",
                StringComparison.Ordinal
            ),
            legacyContent + "\n"
        };

        foreach (var customizedContent in customizedContents)
        {
            WriteTarget(customizedContent);
            var originalBytes = File.ReadAllBytes(TargetPath);

            Assert.False(StartupService.IsEnabled);

            var enableResult = StartupService.Enable();

            Assert.False(enableResult.Success);
            Assert.False(enableResult.IsEnabled);
            Assert.Equal(originalBytes, File.ReadAllBytes(TargetPath));
            AssertRefusalStatus(enableResult);

            var disableResult = StartupService.Disable();

            Assert.False(disableResult.Success);
            Assert.False(disableResult.IsEnabled);
            Assert.Equal(originalBytes, File.ReadAllBytes(TargetPath));
            AssertRefusalStatus(disableResult);
        }
    }

    [Fact]
    public void Marker_owned_stale_entry_updates_and_removes_normally()
    {
        const string staleContent =
            "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=TypeWhisper\n"
            + "Exec=\"/opt/typewhisper-old/typewhisper\" --minimized\n"
            + "Icon=typewhisper-old\n"
            + ManagedLine;
        WriteTarget(staleContent);

        Assert.True(StartupService.IsEnabled);

        var enableResult = StartupService.Enable();

        Assert.True(enableResult.Success);
        Assert.True(enableResult.IsEnabled);
        Assert.Equal(
            CurrentDesktopContent(includeManagedMarker: true),
            File.ReadAllText(TargetPath)
        );
        Assert.True(StartupService.IsEnabled);

        var disableResult = StartupService.Disable();

        Assert.True(disableResult.Success);
        Assert.False(disableResult.IsEnabled);
        Assert.False(File.Exists(TargetPath));
        Assert.False(StartupService.IsEnabled);
    }

    [Fact]
    public void Exact_legacy_entry_migrates_on_enable_and_can_be_removed_directly()
    {
        var legacyContent = CurrentDesktopContent(includeManagedMarker: false);
        WriteTarget(legacyContent);

        Assert.True(StartupService.IsEnabled);

        var enableResult = StartupService.Enable();

        Assert.True(enableResult.Success);
        Assert.True(enableResult.IsEnabled);
        Assert.Equal(
            CurrentDesktopContent(includeManagedMarker: true),
            File.ReadAllText(TargetPath)
        );

        WriteTarget(legacyContent);
        Assert.True(StartupService.IsEnabled);

        var disableResult = StartupService.Disable();

        Assert.True(disableResult.Success);
        Assert.False(disableResult.IsEnabled);
        Assert.False(File.Exists(TargetPath));
        Assert.False(StartupService.IsEnabled);
    }

    [Fact]
    public void Disable_is_a_successful_no_op_when_entry_is_missing()
    {
        var result = StartupService.Disable();

        Assert.True(result.Success);
        Assert.False(result.IsEnabled);
        Assert.False(File.Exists(TargetPath));
        Assert.Equal(Loc.Instance["General.AutostartHint"], result.StatusText);
    }

    [Fact]
    public void IsEnabled_requires_exact_marker_line_or_exact_legacy_content()
    {
        Assert.False(StartupService.IsEnabled);

        WriteTarget("[Desktop Entry]\nName=Foreign\nExec=/usr/bin/foreign");
        Assert.False(StartupService.IsEnabled);

        var markerLookalikes = new[]
        {
            $"[Desktop Entry]\n#{ManagedLine}",
            $"[Desktop Entry]\nPrefix-{ManagedLine}",
            $"[Desktop Entry]\n{ManagedLine}-extra",
            "[Desktop Entry]\nX-TypeWhisper-Managed=false"
        };
        foreach (var markerLookalike in markerLookalikes)
        {
            WriteTarget(markerLookalike);
            Assert.False(StartupService.IsEnabled);
        }

        WriteTarget($"[Desktop Entry]\r\nName=Old TypeWhisper\r\n{ManagedLine}\r\n");
        Assert.True(StartupService.IsEnabled);

        WriteTarget(CurrentDesktopContent(includeManagedMarker: false));
        Assert.True(StartupService.IsEnabled);

        WriteTarget(CurrentDesktopContent(includeManagedMarker: true));
        Assert.True(StartupService.IsEnabled);
    }

    private string TargetPath => Path.Join(_tempDir, "autostart", "typewhisper.desktop");

    private void WriteTarget(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
        File.WriteAllText(TargetPath, content);
    }

    private static string CurrentDesktopContent(bool includeManagedMarker)
    {
        var execPath =
            Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        var iconPath = Path.Join(AppContext.BaseDirectory, "Resources", "typewhisper-128.png");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.Join(AppContext.BaseDirectory, "Resources", "typewhisper-64.png");
        }

        if (!File.Exists(iconPath))
        {
            iconPath = "typewhisper";
        }

        return StartupService.BuildDesktopFile(execPath, iconPath, includeManagedMarker);
    }

    private void AssertRefusalStatus(StartupOperationResult result)
    {
        Assert.Equal(
            Loc.Instance.GetString("General.AutostartEntryPreserved", TargetPath),
            result.StatusText
        );
        Assert.Contains(TargetPath, result.StatusText, StringComparison.Ordinal);
        Assert.Contains("left", result.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "foreign or customized",
            result.StatusText,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("untouched", result.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enabled", result.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("success", result.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
