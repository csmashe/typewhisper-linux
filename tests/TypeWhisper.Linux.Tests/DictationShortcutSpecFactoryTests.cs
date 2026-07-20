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
    [InlineData("Ctrl+Shift+Escape")]
    [InlineData("Escape")]
    [InlineData("ctrl + shift + escape")]
    public void Build_PushToTalk_EscapeEndingTrigger_DropsCancelBindInsteadOfDuplicatingTrigger(
        string trigger
    )
    {
        var settings = CreateSettings(RecordingMode.PushToTalk, trigger);

        var spec = Assert.IsType<DeShortcutSpec>(
            DictationShortcutSpecFactory.Build(settings, CreateWriter("hyprland"))
        );

        // Swapping the last key for Escape would reproduce the record trigger, so the cancel
        // bind is dropped rather than firing both commands off one accelerator.
        Assert.Null(spec.OnCancelTrigger);
        Assert.Null(spec.OnCancelCommand);
        Assert.EndsWith("record start", spec.OnPressCommand);
        Assert.EndsWith("record stop", spec.OnReleaseCommand);
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

    private SettingsService CreateSettings(RecordingMode mode, string trigger = "Ctrl+Shift+Space")
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        settings.Save(
            settings.Current with
            {
                Mode = mode,
                ToggleHotkey = trigger
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
