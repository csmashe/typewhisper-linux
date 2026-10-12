using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public class ParakeetModelsTests
{
    [Theory]
    [InlineData("parakeet-tdt-0.6b", true)]
    [InlineData("parakeet-ultra-0.6b", true)]
    [InlineData("PARAKEET-ULTRA-0.6B", true)]
    [InlineData("canary-180m-flash", false)]
    [InlineData("parakeet-tdt-0.6b-v2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsParakeetTdt_MatchesOnlyTheTdtV3Variants(string? modelId, bool expected) =>
        Assert.Equal(expected, ParakeetModels.IsParakeetTdt(modelId));
}
