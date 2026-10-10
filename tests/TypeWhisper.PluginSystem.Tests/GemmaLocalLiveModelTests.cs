using System.Diagnostics;
using Moq;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.GemmaLocal;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Runs only when <c>TYPEWHISPER_GEMMA_ASSET_DIR</c> names a plugin asset directory that
///     already holds <c>Models/gemma4-e2b-it-q4/gemma-4-E2B-it-Q4_K_M.gguf</c> (~3 GB, CPU).
/// </summary>
public sealed class GemmaLocalLiveModelTests
{
    private const string ModelId = "gemma4-e2b-it-q4";
    private const string CleanupPrompt =
        "Fix the grammar and punctuation of the user's dictated text. Keep the wording otherwise.";

    [GemmaLiveModelFact]
    public async Task LoadedModel_AnswersInBatchAndStreamingAndReleasesOnDeactivate()
    {
        using var plugin = new GemmaLocalPlugin();
        await LoadAsync(plugin);

        var batch = await plugin.ProcessAsync(
            CleanupPrompt, "i went to the store yesterday and buyed three apple", ModelId, CancellationToken.None);
        var streamed = string.Concat(await CollectAsync(plugin.ProcessStreamingAsync(
            CleanupPrompt, "i went to the store yesterday and buyed three apple", ModelId, CancellationToken.None)));

        Assert.Contains("bought", batch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bought", streamed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", batch, StringComparison.Ordinal);

        await plugin.DeactivateAsync();
        Assert.Null(plugin.LoadedModelId);
        Assert.False(plugin.IsAvailable);
    }

    [GemmaLiveModelFact]
    public async Task TypedTemplateMarkers_AreTokenizedAsText()
    {
        using var plugin = new GemmaLocalPlugin();
        await LoadAsync(plugin);
        var markers = plugin.Markers!;
        // E2B's tokenizer matches these user-defined markers even without special parsing.
        Assert.Contains("<|channel>", markers.MatchedInPlainText);
        Assert.Contains("<channel|>", markers.MatchedInPlainText);
        string[] typedMarkers =
        [
            GemmaChatFormat.TurnStart, GemmaChatFormat.TurnEnd, GemmaChatFormat.EndOfSequence,
            .. markers.MatchedInPlainText,
        ];
        var typed = "Please keep " + string.Join(" and ", typedMarkers) + " literally.";

        var withMarkers = plugin.TokenizeLoadedPrompt("", typed).Select(t => (int)t).ToArray();
        var reference = plugin.TokenizeLoadedPrompt("", "x").Select(t => (int)t).ToArray();

        // The typed text adds no control tokens beyond the template's own.
        var control = typedMarkers
            .Select(plugin.TokenizeLoadedSpecial)
            .Where(ids => ids.Length == 1)
            .Select(ids => ids[0])
            .ToHashSet();
        Assert.Equal(reference.Count(control.Contains), withMarkers.Count(control.Contains));

        var answer = await plugin.ProcessAsync(
            "Repeat the user's text exactly, without changes.", typed, ModelId, CancellationToken.None);
        Assert.Contains("literally", answer, StringComparison.Ordinal);
    }

    [GemmaLiveModelFact]
    public async Task CancelDuringPrefill_StopsPromptlyAndTheContextStaysUsable()
    {
        using var plugin = new GemmaLocalPlugin();
        await LoadAsync(plugin);
        var longText = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 500));
        Assert.InRange(plugin.TokenizeLoadedPrompt(CleanupPrompt, longText).Length, 4000, 6000);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => plugin.ProcessAsync(CleanupPrompt, longText, ModelId, cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Cancellation took {stopwatch.Elapsed}.");
        var answer = await plugin.ProcessAsync(CleanupPrompt, "hello world how are you", ModelId, CancellationToken.None);
        Assert.Contains("hello", answer, StringComparison.OrdinalIgnoreCase);
    }

    [GemmaLiveModelFact]
    public async Task PromptBeyondTheContextWindow_IsRejectedAsTooLarge()
    {
        using var plugin = new GemmaLocalPlugin();
        await LoadAsync(plugin);
        var tooLong = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 1000));

        var ex = await Assert.ThrowsAsync<PluginRequestException>(
            () => plugin.ProcessAsync(CleanupPrompt, tooLong, ModelId, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, ex.FailureKind);
    }

    /// <summary>
    ///     Loads the staged plugin folder through the host's own loader (separate load context,
    ///     native libraries resolved from the package) when <c>TYPEWHISPER_GEMMA_PLUGIN_DIR</c>
    ///     names a directory holding the deployed <c>com.typewhisper.gemma-local</c> folder.
    /// </summary>
    [GemmaLiveModelFact(RequiresPackagedPlugin = true)]
    public async Task PackagedPlugin_LoadsTheNativeRuntimeAndAnswers()
    {
        var pluginsDir = Environment.GetEnvironmentVariable(GemmaLiveModelFactAttribute.PluginDirVariable)!;
        var assetDir = Environment.GetEnvironmentVariable(GemmaLiveModelFactAttribute.AssetDirVariable)!;
        var loader = new PluginLoader(Path.Join(Path.GetTempPath(), "tw-gemma-plugin-data-" + Guid.NewGuid().ToString("N")));
        var loaded = Assert.Single(
            loader.DiscoverAndLoad([pluginsDir]),
            p => p.Manifest.Id == "com.typewhisper.gemma-local");
        Assert.NotSame(typeof(GemmaLocalPlugin).Assembly, loaded.Instance.GetType().Assembly);

        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(assetDir);
        host.Setup(h => h.PluginAssetDirectory).Returns(assetDir);
        await loaded.Instance.ActivateAsync(host.Object);
        await ((IPluginSettingsProvider)loaded.Instance).SetSettingValueAsync("selectedModel", ModelId);
        var llm = (ILlmProviderPlugin)loaded.Instance;
        Assert.True(llm.IsAvailable);

        var answer = await llm.ProcessAsync(CleanupPrompt, "thank you for you're help", ModelId, CancellationToken.None);

        Assert.Contains("your", answer, StringComparison.OrdinalIgnoreCase);
        await loaded.Instance.DeactivateAsync();
        loaded.Instance.Dispose();
    }

    private static async Task LoadAsync(GemmaLocalPlugin plugin)
    {
        var assetDir = Environment.GetEnvironmentVariable(GemmaLiveModelFactAttribute.AssetDirVariable)!;
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(assetDir);
        host.Setup(h => h.PluginAssetDirectory).Returns(assetDir);
        await plugin.ActivateAsync(host.Object);
        plugin.SelectModel(ModelId);
        await plugin.LoadModelAsync(ModelId, CancellationToken.None);
        Assert.Equal(ModelId, plugin.LoadedModelId);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> pieces)
    {
        var list = new List<string>();
        await foreach (var piece in pieces)
            list.Add(piece);
        return list;
    }
}

public sealed class GemmaLiveModelFactAttribute : FactAttribute
{
    public const string AssetDirVariable = "TYPEWHISPER_GEMMA_ASSET_DIR";
    public const string PluginDirVariable = "TYPEWHISPER_GEMMA_PLUGIN_DIR";

    public GemmaLiveModelFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AssetDirVariable)))
            Skip = $"Set {AssetDirVariable} to run real Gemma model tests.";
    }

    public bool RequiresPackagedPlugin
    {
        get;
        set
        {
            field = value;
            if (value && Skip is null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PluginDirVariable)))
                Skip = $"Set {PluginDirVariable} to the staged Plugins directory to run the packaged load test.";
        }
    }
}
