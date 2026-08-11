using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AppShutdownOrderTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void HttpApiDrainResult_DrivesDependencyAndProviderDisposal(
        bool httpApiDrained,
        bool expectedDisposeDependencies,
        bool expectedSkipProviderDisposal
    )
    {
        try
        {
            var decision = App.ApplyHttpApiDrainResult(httpApiDrained);

            Assert.Equal(expectedDisposeDependencies, decision.DisposeDependencies);
            Assert.Equal(expectedSkipProviderDisposal, decision.SkipProviderDisposal);
            Assert.Equal(expectedSkipProviderDisposal, App.SkipProviderDisposal);
        }
        finally
        {
            App.ResetShutdownDisposalDecisionForTests();
        }
    }
}
