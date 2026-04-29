using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SnippetsSectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public SnippetsSectionViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.Snippets.Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void SaveSnippet_PersistsSelectedTriggerMode()
    {
        var service = CreateSnippetService();
        var sut = new SnippetsSectionViewModel(service);

        sut.BeginCreateCommand.Execute(null);
        sut.NewTrigger = "sign off";
        sut.NewReplacement = "Best regards,\nChris";
        sut.SelectedTriggerMode = SnippetTriggerMode.ExactPhrase;
        sut.SaveSnippetCommand.Execute(null);

        var snippet = Assert.Single(service.Snippets);
        Assert.Equal(SnippetTriggerMode.ExactPhrase, snippet.TriggerMode);
    }

    [Fact]
    public void SaveSnippet_WhenEditing_PreservesUsageMetadata()
    {
        var service = CreateSnippetService();
        var lastUsedAt = DateTime.UtcNow.AddDays(-1);
        var existing = new Snippet
        {
            Id = "snippet-1",
            Trigger = "addr",
            Replacement = "Old address",
            TriggerMode = SnippetTriggerMode.Anywhere,
            UsageCount = 4,
            LastUsedAt = lastUsedAt,
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        };
        service.AddSnippet(existing);
        var sut = new SnippetsSectionViewModel(service);

        sut.BeginEditCommand.Execute(existing);
        sut.NewReplacement = "New address";
        sut.SelectedTriggerMode = SnippetTriggerMode.ExactPhrase;
        sut.SaveSnippetCommand.Execute(null);

        var snippet = Assert.Single(service.Snippets);
        Assert.Equal("New address", snippet.Replacement);
        Assert.Equal(SnippetTriggerMode.ExactPhrase, snippet.TriggerMode);
        Assert.Equal(4, snippet.UsageCount);
        Assert.Equal(lastUsedAt, snippet.LastUsedAt);
        Assert.Equal(existing.CreatedAt, snippet.CreatedAt);
    }

    [Fact]
    public void PreviewText_UsesSnippetPlaceholderExpansion()
    {
        var service = CreateSnippetService();
        var sut = new SnippetsSectionViewModel(service);

        sut.NewReplacement = "Today is {date:yyyy-MM-dd}";

        Assert.True(sut.ShowPreview);
        Assert.Equal($"Today is {DateTime.Now:yyyy-MM-dd}", sut.PreviewText);
    }

    private SnippetService CreateSnippetService() =>
        new(Path.Combine(_tempDir, "snippets.json"));

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
