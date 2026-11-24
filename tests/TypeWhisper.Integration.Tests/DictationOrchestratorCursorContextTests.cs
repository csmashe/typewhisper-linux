using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorCursorContextTests
{
    private static readonly AtSpiElementRef s_field = new("app", "/field");

    [Theory]
    [InlineData("Hello world.", "", "This is a test.", " This is a test. ")]
    [InlineData("I will ", " tomorrow", "Begin.", "begin")]
    [InlineData("", "", "name@example.com.", "name@example.com")]
    [Trait("Category", "Integration")]
    public Task ReadableField_FitsThePasteToTheTextAroundTheCursor(
        string before, string after, string dictated, string expected)
    {
        return RunAsync(
            fixture => fixture.AtSpi.CaretContext = new DictationInsertionContext(before, after),
            dictated,
            (fixture, delivered) =>
            {
                Assert.Equal(expected, delivered);
                Assert.Equal(1, fixture.AtSpi.CaretContextReadCount);
            });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task StripFinalPeriodOff_KeepsTheValuesPeriod()
    {
        return RunAsync(
            fixture =>
            {
                fixture.Settings.Update(settings => settings with { StripFinalPeriodFromStandaloneValues = false });
                fixture.AtSpi.CaretContext = new DictationInsertionContext("", "");
            },
            "name@example.com.",
            (_, delivered) => Assert.Equal("name@example.com. ", delivered));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task UnreadableField_KeepsTheTrailingSpace()
    {
        return RunAsync(
            fixture => fixture.AtSpi.CaretContext = null,
            "Dictated text.",
            (fixture, delivered) =>
            {
                Assert.Equal("Dictated text. ", delivered);
                Assert.Equal(1, fixture.AtSpi.CaretContextReadCount);
            });
    }

    public static TheoryData<string> NotReadCases =>
        new("consent", "setting", "stopped", "focus", "live-focus", "locked", "copy-only");

    [Theory]
    [MemberData(nameof(NotReadCases))]
    [Trait("Category", "Integration")]
    public Task FieldIsNotRead_WithoutConsentOrWhenItWouldNotBePasted(string reason)
    {
        return RunAsync(
            fixture =>
            {
                fixture.AtSpi.CaretContext = new DictationInsertionContext("Hello world.", "");
                switch (reason)
                {
                    case "consent":
                        fixture.Settings.Update(settings => settings with { TargetAppCorrectionLearningEnabled = false });
                        break;
                    case "setting":
                        fixture.Settings.Update(settings => settings with { FitDictationToCursorContext = false });
                        break;
                    case "live-focus":
                        fixture.AtSpi.FocusedResult = null;
                        break;
                    case "locked":
                        fixture.Settings.Update(settings => settings with { LockPasteToFocusedField = true });
                        break;
                    case "copy-only":
                        fixture.Settings.Update(settings => settings with
                        {
                            AppInsertionStrategies = new Dictionary<string, TextInsertionStrategy>
                            {
                                ["gedit"] = TextInsertionStrategy.CopyOnly,
                            },
                        });
                        break;
                }
            },
            "This is a test.",
            (fixture, delivered) =>
            {
                Assert.Equal("This is a test. ", delivered);
                Assert.Equal(0, fixture.AtSpi.CaretContextReadCount);
            },
            afterStart: fixture =>
            {
                switch (reason)
                {
                    case "stopped":
                        fixture.AtSpi.IsRunning = false;
                        break;
                    case "focus":
                        fixture.AtSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/other");
                        break;
                }
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public Task FocusLostOrClientStoppedDuringTheRead_FallsBackToTheClipboard(bool clientStopped)
    {
        return RunAsync(
            fixture =>
            {
                fixture.AtSpi.CaretContext = new DictationInsertionContext("Hello world.", "");
                var atSpi = fixture.AtSpi;
                atSpi.OnCaretContextRead = clientStopped
                    ? () => atSpi.IsRunning = false
                    : () => atSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/other");
            },
            "This is a test.",
            (fixture, delivered) =>
            {
                Assert.Equal("This is a test. ", delivered);
                Assert.Equal(1, fixture.AtSpi.CaretContextReadCount);
                Assert.Empty(fixture.InsertionPlatform.Typed);
                Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
            });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ConsentWithdrawnDuringTheRead_DropsTheContext()
    {
        return RunAsync(
            fixture =>
            {
                fixture.AtSpi.CaretContext = new DictationInsertionContext("Hello world.", "");
                var settings = fixture.Settings;
                fixture.AtSpi.OnCaretContextRead = () =>
                    settings.Update(current => current with { TargetAppCorrectionLearningEnabled = false });
            },
            "This is a test.",
            (fixture, delivered) =>
            {
                Assert.Equal("This is a test. ", delivered);
                Assert.Equal(1, fixture.AtSpi.CaretContextReadCount);
            });
    }

    private static Task RunAsync(
        Action<OrchestratorCompositionFixture> arrange,
        string dictated,
        Action<OrchestratorCompositionFixture, string?> assert,
        Action<OrchestratorCompositionFixture>? afterStart = null)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(focusedApp: ("gedit", "Editor"));
            // Keep the learner's listener lifecycle separate from these assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with { TargetAppCorrectionLearningEnabled = true });
            fixture.AtSpi.IsRunning = true;
            fixture.AtSpi.CurrentFocusedElement = s_field;
            arrange(fixture);
            fixture.Plugin.EnqueueText(dictated);

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            afterStart?.Invoke(fixture);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            var typed = fixture.InsertionPlatform.Typed;
            var clipboardWrites = fixture.InsertionPlatform.ClipboardWrites;
            var delivered = typed.Count > 0 ? string.Concat(typed) : clipboardWrites.Count > 0 ? clipboardWrites[0] : null;
            assert(fixture, delivered);
            Assert.Equal(0, fixture.AtSpi.StartRequestCount);
        });
    }
}
