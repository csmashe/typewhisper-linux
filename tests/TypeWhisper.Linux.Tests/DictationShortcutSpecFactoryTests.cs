using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationShortcutSpecFactoryTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.Linux.DictationShortcutSpecFactoryTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Theory]
    [InlineData("hyprland")]
    [InlineData("sway")]
    [InlineData("gnome")]
    [InlineData("kde")]
    public void Build_Toggle_ReturnsPressOnlySpecForEveryWriter(string writerId)
    {
        var settings = CreateSettings(RecordingMode.Toggle);

        var spec = Assert.IsType<DeShortcutSpec>(
            DictationShortcutSpecFactory.Build(settings, CreateWriter(writerId))
        );

        Assert.Null(spec.OnReleaseCommand);
        Assert.Null(spec.OnCancelTrigger);
        Assert.Null(spec.OnCancelCommand);
        Assert.DoesNotContain("record start", spec.OnPressCommand);
        Assert.DoesNotContain("record stop", spec.OnPressCommand);
    }

    [Theory]
    [InlineData("hyprland")]
    [InlineData("sway")]
    public void Build_PushToTalk_ReturnsPressReleaseCancelSpecForCapableWriter(string writerId)
    {
        var settings = CreateSettings(RecordingMode.PushToTalk);

        var spec = Assert.IsType<DeShortcutSpec>(
            DictationShortcutSpecFactory.Build(settings, CreateWriter(writerId))
        );

        Assert.EndsWith("record start", spec.OnPressCommand);
        Assert.EndsWith("record stop", spec.OnReleaseCommand);
        Assert.EndsWith("record cancel", spec.OnCancelCommand);
        Assert.NotEqual(spec.Trigger, spec.OnCancelTrigger);
    }

    [Theory]
    [InlineData("gnome")]
    [InlineData("kde")]
    public void Build_PushToTalk_ReturnsNullForPressOnlyWriter(string writerId)
    {
        var settings = CreateSettings(RecordingMode.PushToTalk);

        var spec = DictationShortcutSpecFactory.Build(settings, CreateWriter(writerId));

        Assert.Null(spec);
    }

    [Theory]
    [InlineData("hyprland")]
    [InlineData("sway")]
    [InlineData("gnome")]
    [InlineData("kde")]
    public void Build_Hybrid_ReturnsNullForEveryWriter(string writerId)
    {
        var settings = CreateSettings(RecordingMode.Hybrid);

        var spec = DictationShortcutSpecFactory.Build(settings, CreateWriter(writerId));

        Assert.Null(spec);
    }

    private SettingsService CreateSettings(RecordingMode mode)
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        settings.Save(
            settings.Current with
            {
                Mode = mode,
                ToggleHotkey = "Ctrl+Shift+Space"
            }
        );
        return settings;
    }

    private static IDeShortcutWriter CreateWriter(string writerId)
    {
        return writerId switch
        {
            "hyprland" => new HyprlandShortcutWriter(),
            "sway" => new SwayShortcutWriter(),
            "gnome" => new GnomeShortcutWriter(),
            "kde" => new KdeShortcutWriter(),
            _ => throw new ArgumentOutOfRangeException(nameof(writerId), writerId, null)
        };
    }
}
