using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AppShutdownOrderTests
{
    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, true)]
    public void DrainResults_DriveDependencyAndProviderDisposal(
        bool httpApiDrained,
        bool recorderDrained,
        bool expectedDisposeDependencies,
        bool expectedSkipProviderDisposal
    )
    {
        try
        {
            var decision = App.ApplyHttpApiDrainResult(httpApiDrained, recorderDrained);

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
