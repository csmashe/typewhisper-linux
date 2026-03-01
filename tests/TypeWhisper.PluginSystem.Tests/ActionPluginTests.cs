using Moq;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class ActionPluginTests
{
    [Fact]
    public void ActionContext_PropertiesAreAccessible()
    {
        var context = new ActionContext(
            AppName: "Visual Studio Code",
            ProcessName: "Code",
            Url: "https://example.com",
            Language: "de",
            OriginalText: "Hallo Welt");

        Assert.Equal("Visual Studio Code", context.AppName);
        Assert.Equal("Code", context.ProcessName);
        Assert.Equal("https://example.com", context.Url);
        Assert.Equal("de", context.Language);
        Assert.Equal("Hallo Welt", context.OriginalText);
    }

    [Fact]
    public void ActionContext_NullablePropertiesDefaultToNull()
    {
        var context = new ActionContext(null, null, null, null, null);

        Assert.Null(context.AppName);
        Assert.Null(context.ProcessName);
        Assert.Null(context.Url);
        Assert.Null(context.Language);
        Assert.Null(context.OriginalText);
    }

    [Fact]
    public void ActionResult_SuccessCreation()
    {
        var result = new ActionResult(Success: true, Message: "Done");

        Assert.True(result.Success);
        Assert.Equal("Done", result.Message);
        Assert.Null(result.Url);
        Assert.Null(result.Icon);
        Assert.Equal(3.0, result.DisplayDuration);
    }

    [Fact]
    public void ActionResult_FailureWithCustomDuration()
    {
        var result = new ActionResult(
            Success: false,
            Message: "Failed",
            DisplayDuration: 5.0);

        Assert.False(result.Success);
        Assert.Equal("Failed", result.Message);
        Assert.Equal(5.0, result.DisplayDuration);
    }

    [Fact]
    public void ActionResult_DefaultDisplayDurationIsThreeSeconds()
    {
        var result = new ActionResult(Success: true);
        Assert.Equal(3.0, result.DisplayDuration);
    }

    [Fact]
    public async Task IActionPlugin_MockCanBeCreated()
    {
        var mockAction = new Mock<IActionPlugin>();
        mockAction.Setup(a => a.ActionId).Returns("test-action");
        mockAction.Setup(a => a.ActionName).Returns("Test Action");
        mockAction.Setup(a => a.ActionIcon).Returns("star");
        mockAction.Setup(a => a.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<ActionContext>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionResult(Success: true, Message: "Executed"));

        var context = new ActionContext("App", "proc", null, "en", "hello");
        var result = await mockAction.Object.ExecuteAsync("hello", context, CancellationToken.None);

        Assert.Equal("test-action", mockAction.Object.ActionId);
        Assert.Equal("Test Action", mockAction.Object.ActionName);
        Assert.Equal("star", mockAction.Object.ActionIcon);
        Assert.True(result.Success);
        Assert.Equal("Executed", result.Message);
    }

    [Fact]
    public void ActionContext_RecordEquality()
    {
        var ctx1 = new ActionContext("App", "proc", null, "de", "text");
        var ctx2 = new ActionContext("App", "proc", null, "de", "text");

        Assert.Equal(ctx1, ctx2);
    }

    [Fact]
    public void ActionResult_RecordEquality()
    {
        var r1 = new ActionResult(true, "ok");
        var r2 = new ActionResult(true, "ok");

        Assert.Equal(r1, r2);
    }
}
