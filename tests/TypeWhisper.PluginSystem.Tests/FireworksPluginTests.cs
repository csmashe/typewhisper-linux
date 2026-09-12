using TypeWhisper.Plugin.Fireworks;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class FireworksPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<FireworksPlugin>();

}
