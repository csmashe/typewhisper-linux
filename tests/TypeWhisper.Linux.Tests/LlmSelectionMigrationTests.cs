using Moq;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LlmSelectionMigrationTests
{
    [Theory]
    [InlineData("plugin:com.test.mistral:mistral-small-latest", "plugin:com.test.mistral:mistral-small-2603")]
    [InlineData("plugin:com.test.mistral:mistral-small-2603", "plugin:com.test.mistral:mistral-small-2603")]
    [InlineData("plugin:com.test.mistral:unknown-model", "plugin:com.test.mistral:unknown-model")]
    [InlineData("plugin:com.test.mistral:resolves-unlisted", "plugin:com.test.mistral:resolves-unlisted")]
    [InlineData("plugin:com.test.other:mistral-small-latest", "plugin:com.test.other:mistral-small-latest")]
    [InlineData("plugin:com.test.mistral:", "plugin:com.test.mistral:")]
    [InlineData("not-a-selection", "not-a-selection")]
    [InlineData(null, null)]
    public void Migrate_RewritesOnlySelectionsTheProviderResolvesToAListedModel(string? saved, string? expected)
    {
        Assert.Equal(expected, LlmSelectionMigration.Migrate(saved, [new Provider()]));
    }

    [Fact]
    public void Apply_RewritesTheDefaultAndSpokenCommandSelections()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.test.mistral:mistral-small-latest",
            SpokenCommandLlmProvider = "plugin:com.test.mistral:unknown-model",
        });

        LlmSelectionMigration.Apply([new Provider()], settings.Object);

        Assert.Equal("plugin:com.test.mistral:mistral-small-2603", settings.Object.Current.DefaultLlmProvider);
        Assert.Equal("plugin:com.test.mistral:unknown-model", settings.Object.Current.SpokenCommandLlmProvider);
    }

    [Fact]
    public void Apply_LeavesSettingsUntouchedWhenNothingMoves()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.test.mistral:mistral-small-2603",
        });

        LlmSelectionMigration.Apply([new Provider()], settings.Object);

        settings.Verify(s => s.Update(It.IsAny<Func<AppSettings, AppSettings>>()), Times.Never);
    }

    private sealed class Provider : ILlmProviderRole
    {
        public string PluginId => "com.test.mistral";
        public string ProviderName => "Mistral";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } = [new("mistral-small-2603", "Mistral Small 4")];

        public string? ResolveSavedModelId(string modelId) => modelId switch
        {
            "mistral-small-latest" => "mistral-small-2603",
            "resolves-unlisted" => "mistral-medium-2604",
            _ => null,
        };

        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            Task.FromResult(userText);
    }
}
