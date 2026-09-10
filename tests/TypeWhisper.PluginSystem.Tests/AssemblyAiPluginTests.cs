using TypeWhisper.Plugin.AssemblyAi;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AssemblyAiPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<AssemblyAiPlugin>();

}
