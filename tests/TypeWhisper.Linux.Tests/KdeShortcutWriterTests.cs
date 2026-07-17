// ReSharper disable MethodHasAsyncOverload -- synchronous file operations keep the assertions direct.
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class KdeShortcutWriterTests : IDisposable
{
    private const string ShortcutId = "typewhisper.dictation.toggle";
    private readonly string? _originalXdgDataHome = Environment.GetEnvironmentVariable(
        "XDG_DATA_HOME"
    );
    private readonly string _tempDir = TestPaths.CreateTempDirectory("kde-shortcut-writer");

    public KdeShortcutWriterTests()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalXdgDataHome);
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
    public async Task WriteAsync_writes_the_file_when_none_exists()
    {
        var writer = new KdeShortcutWriter();

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(TargetPath));
        Assert.Equal(ExpectedDesktopContents(), File.ReadAllText(TargetPath));
    }

    [Fact]
    public async Task WriteAsync_overwrites_a_file_it_previously_wrote_with_a_different_trigger()
    {
        var writer = new KdeShortcutWriter();
        var firstResult = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        var result = await writer.WriteAsync(
            CreateSpec("Alt+Shift+Space"),
            CancellationToken.None
        );

        Assert.True(firstResult.Success);
        Assert.True(result.Success);
        Assert.Equal(
            ExpectedDesktopContents("Alt+Shift+Space"),
            File.ReadAllText(TargetPath)
        );
    }

    [Fact]
    public async Task WriteAsync_refuses_to_overwrite_a_foreign_file_at_the_same_path()
    {
        const string foreignContents = "[Desktop Entry]\nName=Foreign shortcut\n";
        WriteTarget(foreignContents);
        var writer = new KdeShortcutWriter();

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(foreignContents, File.ReadAllText(TargetPath));
    }

    [Fact]
    public async Task WriteAsync_refuses_when_marker_present_but_shortcut_id_does_not_match()
    {
        const string mismatchedContents =
            "[Desktop Entry]\nX-TypeWhisper-Managed=true\nX-TypeWhisper-ShortcutId=another.shortcut\n";
        WriteTarget(mismatchedContents);
        var writer = new KdeShortcutWriter();

        var result = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(mismatchedContents, File.ReadAllText(TargetPath));
    }

    [Fact]
    public async Task RemoveAsync_deletes_a_file_it_previously_wrote()
    {
        var writer = new KdeShortcutWriter();
        var writeResult = await writer.WriteAsync(CreateSpec(), CancellationToken.None);

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(writeResult.Success);
        Assert.True(result.Success);
        Assert.False(File.Exists(TargetPath));
    }

    [Fact]
    public async Task RemoveAsync_returns_success_no_op_when_nothing_installed()
    {
        var writer = new KdeShortcutWriter();

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(TargetPath));
    }

    [Fact]
    public async Task RemoveAsync_leaves_a_foreign_file_in_place()
    {
        const string foreignContents = "[Desktop Entry]\nName=Foreign shortcut\n";
        WriteTarget(foreignContents);
        var writer = new KdeShortcutWriter();

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains(TargetPath, result.Warning);
        Assert.True(File.Exists(TargetPath));
        Assert.Equal(foreignContents, File.ReadAllText(TargetPath));
    }

    [Fact]
    public async Task RemoveAsync_leaves_a_managed_file_in_place_when_shortcut_id_does_not_match()
    {
        const string mismatchedContents =
            "[Desktop Entry]\nX-TypeWhisper-Managed=true\nX-TypeWhisper-ShortcutId=another.shortcut\n";
        WriteTarget(mismatchedContents);
        var writer = new KdeShortcutWriter();

        var result = await writer.RemoveAsync(ShortcutId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Warning);
        Assert.Contains(TargetPath, result.Warning);
        Assert.True(File.Exists(TargetPath));
        Assert.Equal(mismatchedContents, File.ReadAllText(TargetPath));
    }

    private string TargetPath => Path.Join(_tempDir, "kglobalaccel", $"{ShortcutId}.desktop");

    private void WriteTarget(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
        File.WriteAllText(TargetPath, contents);
    }

    private static DeShortcutSpec CreateSpec(string trigger = "Ctrl+Shift+Space")
    {
        return new DeShortcutSpec(
            ShortcutId,
            "TypeWhisper Dictation",
            trigger,
            "typewhisper record toggle",
            null,
            null,
            null
        );
    }

    private static string ExpectedDesktopContents(string trigger = "Ctrl+Shift+Space")
    {
        return "[Desktop Entry]\n"
               + "Type=Service\n"
               + "Name=TypeWhisper Dictation\n"
               + "Exec=typewhisper record toggle\n"
               + $"X-KDE-Shortcuts={trigger}\n"
               + "X-KDE-StartupNotify=false\n"
               + "X-TypeWhisper-Managed=true\n"
               + $"X-TypeWhisper-ShortcutId={ShortcutId}\n";
    }
}
