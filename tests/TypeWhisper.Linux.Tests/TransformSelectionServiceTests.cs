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
            SelectionText = "selected text",
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
            SelectionText = "selected text",
        };
        var textInsertion = new TextInsertionService(platform);

        var captured = await TransformSelectionService.CaptureSelectionForTransformAsync(
            textInsertion,
            "firefox"
        );

        Assert.Equal("selected text", captured);
        Assert.False(platform.LastCopyUsedTerminalShortcut);
    }

    [Theory]
    [InlineData("123", "gnome-terminal", "123", "firefox", false)]
    [InlineData("123", "code", "456", "code", true)]
    [InlineData(null, "code", null, "code", false)]
    [InlineData(null, "code", null, "firefox", true)]
    [InlineData(null, null, null, null, false)]
    [InlineData(null, "code", null, null, true)]
    // Asymmetric window id with no process name on either side: the only identity signal
    // appeared or vanished, so the target can no longer be confirmed.
    [InlineData("123", null, null, null, true)]
    [InlineData(null, null, "123", null, true)]
    // Asymmetric window id even when the process names match: identity vanished/appeared on one
    // side, so a same-process match still can't confirm it is the same window.
    [InlineData("123", "code", null, "code", true)]
    [InlineData(null, "code", "123", "code", true)]
    // Captured had a window id and the current target lost every identity signal.
    [InlineData("123", "code", null, null, true)]
    public void HasSelectionTargetChanged_ReturnsExpectedResult(
        string? capturedWindowId,
        string? capturedProcessName,
        string? currentWindowId,
        string? currentProcessName,
        bool expected
    )
    {
        var result = TransformSelectionService.HasSelectionTargetChanged(
            capturedWindowId,
            capturedProcessName,
            currentWindowId,
            currentProcessName
        );

        Assert.Equal(expected, result);
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

    [Fact]
    public async Task DeliverAbortedTransformAsync_CopiesToClipboardOnly_WithoutPasteActivateOrType()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var textInsertion = new TextInsertionService(platform);

        var result = await TransformSelectionService.DeliverAbortedTransformAsync(
            textInsertion,
            "transformed text"
        );

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("transformed text", platform.Clipboard);
        Assert.Equal(0, platform.PasteCalls);
        Assert.Equal(0, platform.ActivateCalls);
        Assert.Equal(0, platform.TypeCalls);
    }

    [Fact]
    public void ProcessingFailure_ProviderCancellationWithLiveDeadline_UsesFailedWarning()
    {
        var message = TransformSelectionService.ProcessingFailureMessage(
            new OperationCanceledException("provider canceled"),
            deadlineExpired: false
        );

        Assert.Equal("Transform selection failed: provider canceled", message);
    }

    [Fact]
    public void ProcessingFailure_PrivateDeadline_UsesTimedOutWarning()
    {
        var message = TransformSelectionService.ProcessingFailureMessage(
            new OperationCanceledException("deadline canceled"),
            deadlineExpired: true
        );

        Assert.Equal("Transform selection timed out.", message);
    }

    [Fact]
    public void ProcessingFailure_DeadlineRacingProviderFault_DeadlineWins()
    {
        var message = TransformSelectionService.ProcessingFailureMessage(
            new HttpRequestException("provider failed"),
            deadlineExpired: true
        );

        Assert.Equal("Transform selection timed out.", message);
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? Clipboard { get; set; }
        public string? SelectionText { get; init; }
        public bool LastCopyUsedTerminalShortcut { get; private set; }
        public int PasteCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public int TypeCalls { get; private set; }
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

        public Task<bool> ClipboardHasNonTextFormatsAsync()
        {
            return Task.FromResult(false);
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
            ActivateCalls++;
            return Task.FromResult(false);
        }

        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false)
        {
            PasteCalls++;
            return Task.FromResult(false);
        }

        public Task<bool> TypeTextAsync(string text)
        {
            TypeCalls++;
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
