using System.Reflection;
using System.Runtime.CompilerServices;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorTargetWindowTests
{
    private static readonly ActiveWindowSnapshot s_editor = new("editor.py", "Editor", "{aaa}", "editor.py", "kwin");

    [Fact]
    public void HasTargetWindowChanged_SameWindow_IsFalse()
    {
        Assert.False(DictationOrchestrator.HasTargetWindowChanged(s_editor, s_editor with { Title = "Editor *" }));
    }

    [Fact]
    public void HasTargetWindowChanged_OtherWindowOfSameProcess_IsTrue()
    {
        Assert.True(DictationOrchestrator.HasTargetWindowChanged(s_editor, s_editor with { WindowId = "{bbb}" }));
    }

    [Fact]
    public void HasTargetWindowChanged_SnapshotLostAtInsertTime_IsTrue()
    {
        Assert.True(DictationOrchestrator.HasTargetWindowChanged(s_editor, null));
    }

    [Fact]
    public void HasTargetWindowChanged_DifferentProvider_IsTrue()
    {
        Assert.True(DictationOrchestrator.HasTargetWindowChanged(s_editor, s_editor with { Source = "xdotool" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HasTargetWindowChanged_NoCapturedIdentity_KeepsDelivering(bool capturedSnapshot)
    {
        var captured = capturedSnapshot ? s_editor with { WindowId = null } : null;
        Assert.False(DictationOrchestrator.HasTargetWindowChanged(captured, s_editor with { WindowId = "{bbb}" }));
    }

    [Fact]
    public async Task IsTargetWindowStillFocused_FocusMovedWithoutXdotoolId_IsFalse()
    {
        var (sut, provider) = Create(s_editor with { WindowId = "{bbb}" });

        Assert.False(await IsStillFocusedAsync(sut, Context(null, s_editor)));
        provider.Verify(p => p.TryGetActiveWindowAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsTargetWindowStillFocused_SameWindowWithoutXdotoolId_IsTrue()
    {
        var (sut, _) = Create(s_editor);

        Assert.True(await IsStillFocusedAsync(sut, Context(null, s_editor)));
    }

    [Fact]
    public async Task IsTargetWindowStillFocused_XdotoolId_LeavesActivationToInsertion()
    {
        var (sut, provider) = Create(s_editor with { WindowId = "{bbb}" });

        Assert.True(await IsStillFocusedAsync(sut, Context("12345", s_editor)));
        provider.Verify(p => p.TryGetActiveWindowAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsTargetWindowStillFocused_XWaylandIdOnWayland_StillChecksCompositorFocus()
    {
        var (sut, _) = Create(s_editor with { WindowId = "{bbb}" });

        Assert.False(await IsStillFocusedAsync(sut, Context("12345", s_editor) with { IsWaylandSession = true }));
    }

    [Theory]
    [InlineData("kwin", true, true)]
    [InlineData("xdotool", true, false)]
    [InlineData("xdotool", false, true)]
    public void CanVerifyTargetWindow_OnWaylandNeedsCompositorSnapshot(string source, bool wayland, bool expected)
    {
        var context = Context(null, s_editor with { Source = source }) with { IsWaylandSession = wayland };

        Assert.Equal(expected, DictationOrchestrator.CanVerifyTargetWindow(context));
    }

    [Fact]
    public async Task IsTargetWindowStillFocused_LockedField_LeavesRestorationToInsertion()
    {
        var (sut, provider) = Create(s_editor with { WindowId = "{bbb}" });
        var context = Context(null, s_editor) with
        {
            LockedFocusField = new LockedFocusTarget(new AtSpiElementRef("app", "/field")),
        };

        Assert.True(await IsStillFocusedAsync(sut, context));
        provider.Verify(p => p.TryGetActiveWindowAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("{aaa}", true)]
    [InlineData("{bbb}", false)]
    public async Task IsVerifiedTargetFocused_GatesTheInitialSelectionProbe(string currentId, bool expected)
    {
        var (sut, _) = Create(s_editor with { WindowId = currentId });
        typeof(DictationOrchestrator).GetField("_textInsertion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, new TextInsertionService(new NoWindowIdPlatform()));
        var context = Context(null, s_editor) with { IsWaylandSession = true };

        var focused = await (Task<bool>)typeof(DictationOrchestrator)
            .GetMethod("IsVerifiedTargetFocusedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, [context])!;

        Assert.Equal(expected, focused);
    }

    [Fact]
    public async Task IsDeliveryTargetStillFocused_OtherFieldOfSameWindow_IsFalse()
    {
        var (sut, _) = Create(s_editor);
        SetFocusedField(sut, new AtSpiElementRef("app", "/field2"));

        Assert.False(await IsStillFocusedAsync(sut, Context(null, s_editor) with { TargetField = s_field }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsDeliveryTargetStillFocused_SameField_IsTrue(bool requireFieldIdentity)
    {
        var (sut, _) = Create(s_editor);
        SetFocusedField(sut, s_field);

        Assert.True(await IsStillFocusedAsync(sut, Context(null, s_editor) with { TargetField = s_field }, requireFieldIdentity));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task IsDeliveryTargetStillFocused_NoFieldIdentity_OnlyPromptResultsCopy(bool requireFieldIdentity, bool expected)
    {
        var (sut, _) = Create(s_editor);
        SetFocusedField(sut, new AtSpiElementRef("app", "/field2"), learningConsent: false);

        Assert.Equal(expected,
            await IsStillFocusedAsync(sut, Context(null, s_editor) with { TargetField = s_field }, requireFieldIdentity));
    }

    [Fact]
    public async Task IsDeliveryTargetStillFocused_WindowMoved_IsFalseEvenWithSameTrackedField()
    {
        var (sut, _) = Create(s_editor with { WindowId = "{bbb}" });
        SetFocusedField(sut, s_field);

        Assert.False(await IsStillFocusedAsync(sut, Context(null, s_editor) with { TargetField = s_field }));
    }

    private static readonly AtSpiElementRef s_field = new("app", "/field1");

    private static void SetFocusedField(DictationOrchestrator sut, AtSpiElementRef focused, bool learningConsent = true)
    {
        var client = new Mock<IAtSpiEventClient>(MockBehavior.Strict);
        client.SetupGet(c => c.IsRunning).Returns(true);
        client.SetupGet(c => c.CurrentFocusedElement).Returns(focused);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings { TargetAppCorrectionLearningEnabled = learningConsent });
        typeof(DictationOrchestrator).GetField("_atSpiClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, client.Object);
        typeof(DictationOrchestrator).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, settings.Object);
    }

    private static (DictationOrchestrator Sut, Mock<IActiveWindowProvider> Provider) Create(ActiveWindowSnapshot? current)
    {
        var sut = (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(typeof(DictationOrchestrator));
        var provider = new Mock<IActiveWindowProvider>();
        provider.Setup(p => p.IsApplicable()).Returns(true);
        provider.Setup(p => p.TryGetActiveWindowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(current);
        var runner = new FakeProcessRunner();
        var activeWindow = new ActiveWindowService([provider.Object], new AtSpiUrlExtractor(runner), runner);
        typeof(DictationOrchestrator).GetField("_activeWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, activeWindow);
        return (sut, provider);
    }

    private static RecordingContext Context(string? windowId, ActiveWindowSnapshot? targetWindow)
    {
        return new RecordingContext(1, DateTime.UtcNow, "editor.py", "Editor", null, windowId, null, "", null,
            false, null, null, LanguageSelection.Automatic, [], CancellationToken.None)
        {
            TargetWindow = targetWindow,
        };
    }

    private static Task<bool> IsStillFocusedAsync(
        DictationOrchestrator sut,
        RecordingContext context,
        bool requireFieldIdentity = false
    )
    {
        return (Task<bool>)typeof(DictationOrchestrator)
            .GetMethod("IsDeliveryTargetStillFocusedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, [context, requireFieldIdentity])!;
    }

    // Wayland without xdotool: no window id to activate, so focus is decided by the snapshot alone.
    private sealed class NoWindowIdPlatform : ITextInsertionPlatform
    {
        public bool IsClipboardSetAvailable => false;
        public bool IsPasteAvailable => false;
        public bool IsKdePlasma => false;
        public bool PrefersDirectTypingForUnknownTarget => false;
        public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;
        public bool LastTypingDeliveredPartialText => false;
        public Task<string?> TryGetClipboardTextAsync() => Task.FromResult<string?>(null);
        public Task<bool> SetClipboardTextAsync(string text) => Task.FromResult(false);
        public Task<bool> ClipboardHasNonTextFormatsAsync() => Task.FromResult(false);
        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;
        public string? GetActiveWindowId() => null;
        public Task<bool> ActivateWindowAsync(string windowId) => Task.FromResult(false);
        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false) => Task.FromResult(false);
        public Task<bool> TypeTextAsync(string text) => Task.FromResult(false);
        public Task<bool> SendCopyAsync(bool useTerminalShortcut) => Task.FromResult(false);
        public Task<bool> SendEnterAsync() => Task.FromResult(false);
    }
}
