using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Integration.Tests;

/// <summary>
///     A browser extension's report picks the website profile at dictation start, also in a browser
///     whose address bar TypeWhisper can't read.
/// </summary>
public sealed class DictationBrowserExtensionContextTests
{
    private const string BrowserProcess = "unlisted-browser";
    private const string BrowserTitle = "Docs — Unlisted Browser";

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("https://docs.example.com/page", "Best regards")]
    [InlineData("https://other.example.com/", "Sig.")]
    [InlineData(null, "Sig.")]
    public Task ReportedTabSelectsTheWebsiteProfile(string? reportedUrl, string expected)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(focusedApp: (BrowserProcess, BrowserTitle));
            SeedWebsiteProfile(fixture);
            var store = fixture.Provider.GetRequiredService<BrowserExtensionContextStore>();
            Assert.NotNull(store.Report(
                "extension-1",
                true,
                reportedUrl,
                "Docs",
                new ActiveWindowSnapshot(BrowserProcess, BrowserTitle, null, null, "integration-scripted")
            ));

            var result = await DictateAsync(fixture, "Sig.");

            Assert.Equal(expected, result.Text);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task WithoutAReportTheWebsiteProfileIsNotSelected()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(focusedApp: (BrowserProcess, BrowserTitle));
            SeedWebsiteProfile(fixture);

            var result = await DictateAsync(fixture, "Sig.");

            Assert.Equal("Sig.", result.Text);
        });
    }

    private static void SeedWebsiteProfile(OrchestratorCompositionFixture fixture)
    {
        fixture.Settings.Update(current => current.WithLanguageHints(["en"]));
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(
            new Profile { Id = "docs", Name = "Docs", UrlPatterns = ["docs.example.com"] });
        fixture.Provider.GetRequiredService<ISnippetService>().AddSnippet(new Snippet
        {
            Id = "docs-sig", Trigger = "sig", Replacement = "Best regards",
            TriggerMode = SnippetTriggerMode.ExactPhrase, ProfileIds = ["docs"],
        });
    }

    private static async Task<DictationSessionResult> DictateAsync(OrchestratorCompositionFixture fixture, string transcript)
    {
        fixture.Plugin.EnqueueText(transcript);
        var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        Assert.True(sessionId > 0);
        fixture.FeedNonSilentAudio();
        var resultTask = fixture.WaitForResultAsync(sessionId);
        await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
        return await BoundedTest.WaitAsync(resultTask);
    }
}
