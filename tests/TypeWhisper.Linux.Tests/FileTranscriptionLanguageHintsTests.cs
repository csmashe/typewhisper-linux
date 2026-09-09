using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>Covers how a queued file resolves its ordered language hints from options and settings.</summary>
public sealed class FileTranscriptionLanguageHintsTests
{
    private static readonly AppSettings s_settings = AppSettings.Default.WithLanguageHints(["de", "en"]);

    [Fact]
    public void ResolveLanguageHints_ExplicitLanguage_Wins() =>
        Assert.Equal(["fr"], FileTranscriptionProcessor.ResolveLanguageHints(new FileTranscriptionProcessOptions(Language: "fr", LanguageHints: ["de"]), s_settings));

    [Fact]
    public void ResolveLanguageHints_AutoLanguage_IsAutomatic() =>
        Assert.Empty(FileTranscriptionProcessor.ResolveLanguageHints(new FileTranscriptionProcessOptions(Language: "auto"), s_settings));

    [Fact]
    public void ResolveLanguageHints_SuppliedList_IsUsedAsIs() =>
        Assert.Equal(["fr", "it"], FileTranscriptionProcessor.ResolveLanguageHints(new FileTranscriptionProcessOptions(LanguageHints: [" fr ", "it", "FR"]), s_settings));

    [Fact]
    public void ResolveLanguageHints_EmptySuppliedList_StaysAutomatic() =>
        Assert.Empty(FileTranscriptionProcessor.ResolveLanguageHints(new FileTranscriptionProcessOptions(LanguageHints: []), s_settings));

    [Fact]
    public void ResolveLanguageHints_NoList_InheritsSettings()
    {
        Assert.Equal(["de", "en"], FileTranscriptionProcessor.ResolveLanguageHints(new FileTranscriptionProcessOptions(), s_settings));
        Assert.Equal(["de", "en"], FileTranscriptionProcessor.ResolveLanguageHints(null, s_settings));
    }
}
