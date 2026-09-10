using TypeWhisper.Plugin.Cohere;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CoherePluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<CoherePlugin>();

}
