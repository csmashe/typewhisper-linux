using TypeWhisper.Plugin.OpenAi;
using Xunit;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>The ChatGPT catalog parser behind <c>RefreshAvailableLlmModels</c> in ChatGPT mode.</summary>
public sealed class OpenAiChatGptCatalogParserTests
{
    [Fact]
    public void CatalogShape_KeepsPlanAvailabilityAndPriorityForPlanFiltering()
    {
        const string json = """
            {"models":[
              {"slug":"gpt-5.6-sol","display_name":"Sol","visibility":"list","priority":2,"available_in_plans":["pro"]},
              {"slug":"gpt-5.6-luna","visibility":"list","available_in_plans":["plus","pro"]}
            ]}
            """;

        Assert.True(OpenAiPlugin.TryReadChatGptModels(json, out var models));

        var sol = Assert.Single(models, model => model!.Slug == "gpt-5.6-sol")!;
        Assert.Equal(["pro"], sol.AvailableInPlans);
        Assert.Equal(2, sol.Priority);
        Assert.Equal("Sol", sol.DisplayName);
    }

    [Fact]
    public void MalformedEntriesAreSkippedInsteadOfDiscardingTheCatalog()
    {
        const string json = """
            {"models":[null, 7, {"slug":42,"visibility":"list"}, {"slug":"gpt-ok","visibility":"list","available_in_plans":"pro"},
                       {"slug":"gpt-hidden","visibility":"hide"}]}
            """;

        Assert.True(OpenAiPlugin.TryReadChatGptModels(json, out var models));

        var model = Assert.Single(models)!;
        Assert.Equal("gpt-ok", model.Slug);
        Assert.Null(model.AvailableInPlans);
    }

    [Fact]
    public void StandardModelsShape_IsReadAsChatModels()
    {
        // ChatGptModelsEndpoint is /v1/models, which can answer in the standard list shape.
        const string json = """
            {"object":"list","data":[{"id":"gpt-5.6-sol","object":"model"},{"id":"text-embedding-3-large","object":"model"},
                                     {"id":7}, null]}
            """;

        Assert.True(OpenAiPlugin.TryReadChatGptModels(json, out var models));

        var model = Assert.Single(models)!;
        Assert.Equal("gpt-5.6-sol", model.Slug);
        Assert.Equal("list", model.Visibility);
    }

    [Fact]
    public void UnknownShapeIsRejected()
    {
        Assert.False(OpenAiPlugin.TryReadChatGptModels("""{"items":[]}""", out _));
        Assert.False(OpenAiPlugin.TryReadChatGptModels("[]", out _));
    }
}
