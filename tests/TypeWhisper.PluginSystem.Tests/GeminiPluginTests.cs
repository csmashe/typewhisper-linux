using TypeWhisper.Plugin.Gemini;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GeminiPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<GeminiPlugin>();

}
