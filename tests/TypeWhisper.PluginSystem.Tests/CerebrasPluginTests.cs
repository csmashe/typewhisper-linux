using TypeWhisper.Plugin.Cerebras;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CerebrasPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<CerebrasPlugin>();

}
