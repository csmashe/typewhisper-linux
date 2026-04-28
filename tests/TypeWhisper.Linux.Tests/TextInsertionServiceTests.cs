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

    [Fact]
    public async Task InsertTextAsync_missing_clipboard_tool_returns_specific_result()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardSetAvailable = false,
            PasteAvailable = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoPaste: true);

        Assert.Equal(InsertionResult.MissingClipboardTool, result);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_missing_paste_tool_returns_specific_result_when_auto_paste_enabled()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardSetAvailable = true,
            PasteAvailable = false
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoPaste: true);

        Assert.Equal(InsertionResult.MissingPasteTool, result);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_missing_paste_tool_allows_copy_only()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ClipboardSetAvailable = true,
            PasteAvailable = false
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoPaste: false);

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_codex_window_uses_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            autoPaste: true,
            targetWindowTitle: "Codex");

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_codex_process_uses_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform();
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            autoPaste: true,
            targetProcessName: "codex");

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_terminal_process_uses_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform();
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            autoPaste: true,
            targetProcessName: "kitty",
            targetWindowTitle: "typewhisper-linux");

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? Clipboard { get; set; }
        public string? ActiveWindowId { get; set; }
        public bool ClipboardSetAvailable { get; set; } = true;
        public bool PasteAvailable { get; set; } = true;
        public bool ActivateSucceeds { get; set; } = true;
        public bool PasteSucceeds { get; set; } = true;
        public bool PasteSent { get; private set; }
        public string? TypedText { get; private set; }

        public bool IsClipboardSetAvailable => ClipboardSetAvailable;

        public bool IsPasteAvailable => PasteAvailable;

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

        public Task<bool> TypeTextAsync(string text)
        {
            TypedText = text;
            return Task.FromResult(PasteSucceeds);
        }

        public Task<bool> SendCopyAsync() => Task.FromResult(true);

        public Task<bool> SendEnterAsync() => Task.FromResult(true);
    }
}
