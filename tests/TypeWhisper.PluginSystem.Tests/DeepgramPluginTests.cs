using TypeWhisper.Plugin.Deepgram;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class DeepgramPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<DeepgramPlugin>();

}
