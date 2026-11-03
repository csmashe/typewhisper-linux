using System.Runtime.CompilerServices;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ActionPluginExecutionHostTests
{
    [Fact]
    public async Task ExecuteAsync_normalizes_all_fields_and_publishes_them()
    {
        await using var eventBus = new PluginEventBus();
        var completed = new TaskCompletionSource<ActionCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = eventBus.Subscribe<ActionCompletedEvent>(actionEvent =>
        {
            completed.TrySetResult(actionEvent);
            return Task.CompletedTask;
        });
        var host = CreateHost(eventBus, 1500);
        var plugin = new Mock<IActionPlugin>();
        plugin.SetupGet(value => value.ActionId).Returns("linear-create");
        plugin.Setup(value => value.ExecuteAsync(
                "issue body",
                It.IsAny<ActionContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionResult(
                true,
                "Issue created",
                "https://example.com/issues/42",
                "  task-due  ",
                5.0
            ));

        var result = await host.ExecuteAsync(
            plugin.Object,
            "issue body",
            new ActionContext("Editor", "editor", null, "en", "raw"),
            "Editor",
            CancellationToken.None
        );
        var actionEvent = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            new ActionPluginExecutionResult(
                true,
                "Issue created",
                "https://example.com/issues/42",
                "task-due",
                5000
            ),
            result
        );
        Assert.Equal("linear-create", actionEvent.ActionId);
        Assert.True(actionEvent.Success);
        Assert.Equal(result.Message, actionEvent.Message);
        Assert.Equal(result.Url, actionEvent.Url);
        Assert.Equal(result.Icon, actionEvent.Icon);
        Assert.Equal(result.DisplayDurationMilliseconds, actionEvent.DisplayDurationMilliseconds);
        Assert.Equal("Editor", actionEvent.AppName);
    }

    [Fact]
    public void Normalize_rejects_unsafe_url_without_failing_successful_action()
    {
        using var eventBus = new PluginEventBus();
        var errorLog = new Mock<IErrorLogService>();
        var host = CreateHost(eventBus, 1800, errorLog);

        var result = host.Normalize(
            new ActionResult(true, "Created", "file:///tmp/result", DisplayDuration: double.NaN)
        );

        Assert.True(result.Success);
        Assert.Null(result.Url);
        Assert.Equal(1800, result.DisplayDurationMilliseconds);
        errorLog.Verify(
            log => log.AddEntry(
                It.Is<string>(message => message.Contains("file:///tmp/result", StringComparison.Ordinal)),
                ErrorCategory.Plugin
            ),
            Times.Once
        );
    }

    [Theory]
    [InlineData(-1.0, 0)]
    [InlineData(0.1255, 126)]
    [InlineData(20.0, 5000)]
    public void Normalize_clamps_finite_plugin_duration(double seconds, int expectedMilliseconds)
    {
        using var eventBus = new PluginEventBus();
        var result = CreateHost(eventBus, 1500).Normalize(
            new ActionResult(true, "Done", DisplayDuration: seconds)
        );

        Assert.Equal(expectedMilliseconds, result.DisplayDurationMilliseconds);
    }

    [Fact]
    public void Normalize_global_zero_is_absolute_feedback_opt_out()
    {
        using var eventBus = new PluginEventBus();
        var result = CreateHost(eventBus, 0).Normalize(
            new ActionResult(true, "Done", DisplayDuration: 5.0)
        );

        Assert.Equal(0, result.DisplayDurationMilliseconds);
    }

    [Fact]
    public void Normalize_preserves_nonblank_plugin_message_verbatim()
    {
        using var eventBus = new PluginEventBus();

        var result = CreateHost(eventBus, 1500).Normalize(
            new ActionResult(true, "  Plugin-formatted message  ")
        );

        Assert.Equal("  Plugin-formatted message  ", result.Message);
    }

    [Theory]
    [InlineData(true, "Action completed.")]
    [InlineData(false, "Action failed.")]
    public void Normalize_blank_message_uses_localized_generic(bool success, string expected)
    {
        InitializeEnglishLocalization();
        using var eventBus = new PluginEventBus();

        var result = CreateHost(eventBus, 1500).Normalize(new ActionResult(success, "  "));

        Assert.Equal(expected, result.Message);
    }

    private static ActionPluginExecutionHost CreateHost(
        PluginEventBus eventBus,
        int globalDuration,
        Mock<IErrorLogService>? errorLog = null
    )
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(value => value.Current).Returns(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = globalDuration }
        );
        return new ActionPluginExecutionHost(
            eventBus,
            settings.Object,
            (errorLog ?? new Mock<IErrorLogService>()).Object
        );
    }

    private static void InitializeEnglishLocalization([CallerFilePath] string thisFile = "")
    {
        var testDirectory = Path.GetDirectoryName(thisFile)!;
        var localizationDirectory = Path.GetFullPath(
            Path.Join(
                testDirectory,
                "..",
                "..",
                "src",
                "TypeWhisper.Linux",
                "Resources",
                "Localization"
            )
        );
        Loc.Instance.Initialize(localizationDirectory);
        Loc.Instance.CurrentLanguage = "en";
    }
}
