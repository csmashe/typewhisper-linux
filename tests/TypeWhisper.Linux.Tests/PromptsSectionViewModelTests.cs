using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PromptsSectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public PromptsSectionViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.Linux.PromptVmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void SaveAction_PersistsProviderOverrideAndTargetAction()
    {
        var prompts = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, pluginManager, settings.Object);
        sut.StartCreateCommand.Execute(null);
        sut.EditName = "Rewrite";
        sut.EditSystemPrompt = "Rewrite this";
        sut.EditProviderOverride = "plugin:com.typewhisper.openai:gpt-4.1-mini";
        sut.EditTargetActionPluginId = "com.typewhisper.linear";
        sut.SaveActionCommand.Execute(null);

        var action = Assert.Single(prompts.Actions);
        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", action.ProviderOverride);
        Assert.Equal("com.typewhisper.linear", action.TargetActionPluginId);
    }

    [Fact]
    public void SelectedEditProvider_UpdatesProviderOverride()
    {
        var prompts = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));
        var provider = new FakeLlmProviderPlugin("com.typewhisper.openai", "OpenAI", "gpt-4.1-mini");
        using var pluginManager = TestPluginManagerFactory.Create(
            llmProviders: [provider],
            loadedPlugins: [TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, provider.PluginId, provider)]);
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, pluginManager, settings.Object);
        var option = Assert.Single(sut.AvailableProviders, candidate =>
            candidate.Value == "plugin:com.typewhisper.openai:gpt-4.1-mini");

        sut.SelectedEditProvider = option;

        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", sut.EditProviderOverride);
        Assert.Equal(option, sut.SelectedEditProvider);
    }

    [Fact]
    public void SelectedEditProvider_IgnoresTransientSelectionChangesDuringProviderRefresh()
    {
        var prompts = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));
        var provider = new FakeLlmProviderPlugin("com.typewhisper.openai", "OpenAI", "gpt-4.1-mini");
        using var pluginManager = TestPluginManagerFactory.Create(
            llmProviders: [provider],
            loadedPlugins: [TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, provider.PluginId, provider)]);
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var sut = new PromptsSectionViewModel(prompts, pluginManager, settings.Object)
        {
            EditProviderOverride = "plugin:com.typewhisper.openai:gpt-4.1-mini"
        };
        SetPrivateField(sut, "_isRefreshingProviders", true);

        sut.SelectedEditProvider = null;

        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", sut.EditProviderOverride);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeLlmProviderPlugin : ILlmProviderPlugin
    {
        public FakeLlmProviderPlugin(string pluginId, string providerName, string modelId)
        {
            PluginId = pluginId;
            ProviderName = providerName;
            SupportedModels = [new PluginModelInfo(modelId, "GPT-4.1 Mini")];
        }

        public string PluginId { get; }
        public string PluginName => ProviderName;
        public string PluginVersion => "1.0.0";
        public string ProviderName { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            Task.FromResult(userText);

        public void Dispose()
        {
        }
    }
}
