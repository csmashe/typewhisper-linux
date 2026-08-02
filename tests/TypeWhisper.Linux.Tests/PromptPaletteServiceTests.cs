using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PromptPaletteServiceTests
{
    [Fact]
    public async Task Action_plugin_declared_failure_is_passed_to_shared_presenter()
    {
        await using var eventBus = new PluginEventBus();
        var host = CreateHost(eventBus);
        var plugin = CreatePlugin(new ActionResult(false, "Linear rejected the issue"));
        ActionPluginExecutionResult? presented = null;

        var result = await PromptPaletteService.ExecuteActionPluginAsync(
            host,
            plugin,
            "processed issue",
            "captured selection",
            value =>
            {
                presented = value;
                return true;
            },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.NotNull(presented);
        Assert.Same(result, presented);
        Assert.Equal("Linear rejected the issue", presented.Message);
    }

    [Fact]
    public async Task Action_plugin_success_is_passed_to_shared_presenter()
    {
        await using var eventBus = new PluginEventBus();
        var host = CreateHost(eventBus);
        var plugin = CreatePlugin(new ActionResult(true, "Linear issue TW-42 created"));
        ActionPluginExecutionResult? presented = null;

        var result = await PromptPaletteService.ExecuteActionPluginAsync(
            host,
            plugin,
            "processed issue",
            "captured selection",
            value =>
            {
                presented = value;
                return true;
            },
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Same(result, presented);
    }

    /// <summary>
    ///     Decline (e.g. a live dictation owns the overlay) is the publisher's call; the
    ///     helper must still hand over every result, unchanged.
    /// </summary>
    [Fact]
    public async Task Declined_publication_still_offers_the_failure_and_returns_it()
    {
        await using var eventBus = new PluginEventBus();
        var host = CreateHost(eventBus);
        var plugin = CreatePlugin(new ActionResult(false, "Linear rejected the issue"));
        var offered = new List<ActionPluginExecutionResult>();

        var result = await PromptPaletteService.ExecuteActionPluginAsync(
            host,
            plugin,
            "processed issue",
            "captured selection",
            value =>
            {
                offered.Add(value);
                return false;
            },
            CancellationToken.None
        );

        Assert.Same(result, Assert.Single(offered));
        Assert.False(result.Success);
        Assert.Equal("Linear rejected the issue", result.Message);
    }

    /// <summary>
    ///     Pins production wiring to a bare method group
    ///     (<see cref="DictationOrchestrator.TryPublishActionFeedback" />), not a lambda that
    ///     could quietly re-silence declared failures. If the signatures drift, the method
    ///     group stops binding and this test fails first.
    /// </summary>
    [Fact]
    public void Feedback_publisher_parameter_binds_the_orchestrator_method_group()
    {
        var publisher = typeof(DictationOrchestrator).GetMethod(
            nameof(DictationOrchestrator.TryPublishActionFeedback),
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(publisher);

        var parameter = typeof(PromptPaletteService)
            .GetMethod(
                nameof(PromptPaletteService.ExecuteActionPluginAsync),
                BindingFlags.Static | BindingFlags.NonPublic
            )!
            .GetParameters()
            .Single(value => typeof(Delegate).IsAssignableFrom(value.ParameterType));

        var invoke = parameter.ParameterType.GetMethod("Invoke")!;
        Assert.Equal(publisher.ReturnType, invoke.ReturnType);
        Assert.Equal(
            publisher.GetParameters().Select(value => value.ParameterType),
            invoke.GetParameters().Select(value => value.ParameterType)
        );
    }

    [Fact]
    public void ProviderCancellationWithLiveTokens_IsDependencyFault()
    {
        Assert.Equal(
            PromptPaletteCancellationOrigin.DependencyFault,
            PromptPaletteService.ClassifyCancellationOrigin(
                new OperationCanceledException("provider canceled"),
                CancellationToken.None,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public void PrivateStreamDeadline_IsTimeoutSoCallerCanFallBackToBatch()
    {
        using var deadlineCts = new CancellationTokenSource();
        deadlineCts.Cancel();

        Assert.Equal(
            PromptPaletteCancellationOrigin.PrivateDeadline,
            PromptPaletteService.ClassifyCancellationOrigin(
                new OperationCanceledException(deadlineCts.Token),
                CancellationToken.None,
                deadlineCts.Token
            )
        );
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("action")]
    public void PrivateBatchOrActionDeadline_IsQuietBoundedTimeout(string boundary)
    {
        using var deadlineCts = new CancellationTokenSource();
        deadlineCts.Cancel();

        var origin = PromptPaletteService.ClassifyCancellationOrigin(
            new OperationCanceledException($"{boundary} deadline"),
            CancellationToken.None,
            deadlineCts.Token
        );

        Assert.Equal(PromptPaletteCancellationOrigin.PrivateDeadline, origin);
    }

    [Fact]
    public void GenuineUserAbort_Wins()
    {
        using var userCts = new CancellationTokenSource();
        userCts.Cancel();

        Assert.Equal(
            PromptPaletteCancellationOrigin.UserCancellation,
            PromptPaletteService.ClassifyCancellationOrigin(
                new OperationCanceledException(userCts.Token),
                userCts.Token,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public void UserAbortAndPrivateDeadlineRace_UserWins()
    {
        using var userCts = new CancellationTokenSource();
        using var deadlineCts = new CancellationTokenSource();
        userCts.Cancel();
        deadlineCts.Cancel();

        Assert.Equal(
            PromptPaletteCancellationOrigin.UserCancellation,
            PromptPaletteService.ClassifyCancellationOrigin(
                new TimeoutException("both fired"),
                userCts.Token,
                deadlineCts.Token
            )
        );
    }

    private static ActionPluginExecutionHost CreateHost(PluginEventBus eventBus)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(value => value.Current).Returns(AppSettings.Default);
        return new ActionPluginExecutionHost(
            eventBus,
            settings.Object,
            new Mock<IErrorLogService>().Object
        );
    }

    private static IActionPlugin CreatePlugin(ActionResult result)
    {
        var plugin = new Mock<IActionPlugin>();
        plugin.SetupGet(value => value.ActionId).Returns("linear-create");
        plugin.Setup(value => value.ExecuteAsync(
                "processed issue",
                It.Is<ActionContext>(context => context.OriginalText == "captured selection"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return plugin.Object;
    }
}
