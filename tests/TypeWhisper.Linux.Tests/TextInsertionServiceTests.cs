using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TextInsertionServiceTests
{
    [Fact]
    public async Task InsertTextAsync_successful_auto_paste_restores_previous_clipboard()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous", PasteSucceeds = true };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoPaste: true);

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.Clipboard);
        Assert.True(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_failed_paste_falls_back_to_clipboard()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous", PasteSucceeds = false };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoPaste: true);

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.True(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_copy_only_sets_clipboard_without_restore()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoPaste: false);

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_focus_failure_falls_back_to_clipboard()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ActiveWindowId = "other",
            ActivateSucceeds = false
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoPaste: true, targetWindowId: "target");

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.False(platform.PasteSent);
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? Clipboard { get; set; }
        public string? ActiveWindowId { get; set; }
        public bool ActivateSucceeds { get; set; } = true;
        public bool PasteSucceeds { get; set; } = true;
        public bool PasteSent { get; private set; }

        public Task<string?> TryGetClipboardTextAsync() => Task.FromResult(Clipboard);

        public Task<bool> SetClipboardTextAsync(string text)
        {
            Clipboard = text;
            return Task.FromResult(true);
        }

        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;

        public string? GetActiveWindowId() => ActiveWindowId;

        public Task<bool> ActivateWindowAsync(string windowId)
        {
            if (ActivateSucceeds)
                ActiveWindowId = windowId;
            return Task.FromResult(ActivateSucceeds);
        }

        public Task<bool> SendPasteAsync()
        {
            PasteSent = true;
            return Task.FromResult(PasteSucceeds);
        }

        public Task<bool> SendCopyAsync() => Task.FromResult(true);

        public Task<bool> SendEnterAsync() => Task.FromResult(true);
    }
}
