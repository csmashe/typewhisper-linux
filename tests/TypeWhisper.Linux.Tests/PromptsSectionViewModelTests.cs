using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;
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
}
