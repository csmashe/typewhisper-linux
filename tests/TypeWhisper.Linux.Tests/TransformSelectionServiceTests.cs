using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TransformSelectionServiceTests
{
    [Fact]
    public async Task CaptureSelectionForTransformAsync_UsesTerminalCopyShortcut_ForTerminalProcess()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "selected text"
        };
        var textInsertion = new TextInsertionService(platform);

        var captured = await TransformSelectionService.CaptureSelectionForTransformAsync(
            textInsertion,
            "gnome-terminal-server"
        );

        Assert.Equal("selected text", captured);
        Assert.True(platform.LastCopyUsedTerminalShortcut);
    }

    [Fact]
    public async Task CaptureSelectionForTransformAsync_UsesPlainCopyShortcut_ForNonTerminalProcess()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "selected text"
        };
        var textInsertion = new TextInsertionService(platform);

        var captured = await TransformSelectionService.CaptureSelectionForTransformAsync(
            textInsertion,
            "firefox"
        );

        Assert.Equal("selected text", captured);
        Assert.False(platform.LastCopyUsedTerminalShortcut);
    }

    [Fact]
    public void BuildTransformPrompt_IncludesSelectedTextAndSpokenCommand()
    {
        var result = TransformSelectionService.BuildTransformPrompt(
            "This sentence is too long.",
            "make it concise"
        );

        Assert.Contains("Output ONLY the edited text", result);
        Assert.Contains("This sentence is too long.", result);
        Assert.Contains("make it concise", result);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("Cancel.")]
    [InlineData("never mind")]
    [InlineData("nevermind!")]
    [InlineData("stop")]
    public void IsCancelCommand_ReturnsTrueForCancelPhrases(string command)
    {
        Assert.True(TransformSelectionService.IsCancelCommand(command));
    }

    [Fact]
    public void IsCancelCommand_ReturnsFalseForNormalEditInstruction()
    {
        Assert.False(TransformSelectionService.IsCancelCommand("make this more concise"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCancelCommand_ReturnsFalseForEmptyOrWhitespace(string command)
    {
        Assert.False(TransformSelectionService.IsCancelCommand(command));
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? Clipboard { get; set; }
        public string? SelectionText { get; init; }
        public bool LastCopyUsedTerminalShortcut { get; private set; }
        public bool IsClipboardSetAvailable => true;
        public bool IsPasteAvailable => true;
        public bool IsKdePlasma => false;
        public bool PrefersDirectTypingForUnknownTarget => false;
        public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;
        public bool LastTypingDeliveredPartialText => false;

        public Task<string?> TryGetClipboardTextAsync()
        {
            return Task.FromResult(Clipboard);
        }

        public Task<bool> SetClipboardTextAsync(string text)
        {
            Clipboard = text;
            return Task.FromResult(true);
        }

        public Task DelayAsync(TimeSpan delay)
        {
            return Task.CompletedTask;
        }

        public string? GetActiveWindowId()
        {
            return null;
        }

        public Task<bool> ActivateWindowAsync(string windowId)
        {
            return Task.FromResult(false);
        }

        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false)
        {
            return Task.FromResult(false);
        }

        public Task<bool> TypeTextAsync(string text)
        {
            return Task.FromResult(false);
        }

        public Task<bool> SendCopyAsync(bool useTerminalShortcut)
        {
            LastCopyUsedTerminalShortcut = useTerminalShortcut;
            Clipboard = SelectionText;
            return Task.FromResult(true);
        }

        public Task<bool> SendEnterAsync()
        {
            return Task.FromResult(false);
        }
    }
}
