using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services.Vocabulary;
using TypeWhisper.Linux.Tests.Vocabulary;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.Tests;
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

    [Fact]
    public async Task EligibleRescorer_RefinesPipelineAndSkipsBooster()
    {
        var rescorer = new FakeVocabularyRescorerPlugin();
        var (result, pipeline) = await ProcessAsync("type whisper", EnglishOutputVariant.AsTranscribed, rescorer);
        Assert.Equal("type whisper", result.RawResult.Text);
        Assert.Equal("TypeWhisper", result.ProcessedText);
        pipeline.Verify(
            p =>
                p.ProcessAsync(
                    "TypeWhisper",
                    It.Is<PipelineOptions>(o => o.VocabularyBooster == null),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        Assert.Equal(1, rescorer.CallCount);
    }

    [Fact]
    public async Task Rescorer_SeesEngineTextBeforeOutputNormalization()
    {
        var rescorer = new FakeVocabularyRescorerPlugin
        {
            Handler = (request, _) => Task.FromResult(new VocabularyRescoreResult(request.RecordingId, [])),
        };
        var (result, _) = await ProcessAsync("colour", EnglishOutputVariant.UnitedStates, rescorer);
        Assert.Equal("colour", rescorer.Request?.Text);
        Assert.Equal("color", result.RawResult.Text);
        Assert.Equal("colour", result.ProcessedText);
    }

    private static async Task<(FileTranscriptionProcessResult Result, Mock<IPostProcessingPipeline> Pipeline)> ProcessAsync(
        string engineText,
        EnglishOutputVariant englishOutputVariant,
        FakeVocabularyRescorerPlugin rescorer
    )
    {
        using var plugins = TestPluginManagerFactory.Create(transcriptionEngines: [new TimedTranscriptionEngine(engineText)]);
        PluginManagerTestAccess.SetVocabularyRescorers(plugins, [rescorer]);
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                SelectedModelId = ModelManagerService.GetPluginModelId("test-file", "test"),
                VocabularyBoostingEnabled = true,
                EnglishOutputVariant = englishOutputVariant,
            }
        );
        var dictionary = new Mock<IDictionaryService>();
        dictionary
            .SetupGet(d => d.Entries)
            .Returns([
                new DictionaryEntry
                {
                    Id = "term",
                    Original = "TypeWhisper",
                    EntryType = DictionaryEntryType.Term,
                },
            ]);
        var pipeline = new Mock<IPostProcessingPipeline>();
        pipeline
            .Setup(p =>
                p.ProcessAsync(
                    It.IsAny<string>(),
                    It.IsAny<PipelineOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (string text, PipelineOptions _, CancellationToken _) =>
                    Task.FromResult(new PostProcessingResult { Text = text })
            );
        var runner = new FakeProcessRunner
        {
            SupervisorDefault = new ProcessRunOutcome(
                ProcessRunStatus.Exited,
                0,
                PcmWavTests.CreateWav([0, 123, -123], list: true, dataSize: uint.MaxValue),
                [],
                ProcessOutputStatus.Complete,
                null
            ),
        };
        var commands = new SystemCommandAvailabilityService(runner);
        commands.RaiseSnapshotChangedForTests(commands.GetSnapshot() with { HasFfmpeg = true });
        using var models = new ModelManagerService(plugins, settings.Object);
        var processor = new FileTranscriptionProcessor(
            models,
            settings.Object,
            new AudioFileService(commands, runner),
            dictionary.Object,
            Mock.Of<IVocabularyBoostingService>(),
            new VocabularyRescoringService(plugins, settings.Object, dictionary.Object),
            pipeline.Object
        );
        var path = TestPaths.NewTempPath("vocabulary-file.wav");
        try
        {
            await File.WriteAllBytesAsync(path, [0]);
            var result = await processor.ProcessAsync(path, _ => { }, null, CancellationToken.None);
            return (result, pipeline);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TimedTranscriptionEngine(string text) : ITranscriptionEngineRole
    {
        public string PluginId => "test-file";
        public string ProviderId => "test-file";
        public string ProviderDisplayName => "Test file";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("test", "Test")];
        public string SelectedModelId => "test";
        public bool SupportsTranslation => false;
        public void SelectModel(string modelId) { }
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult(text, "en", 1)
            {
                TokenTimings = [new VocabularyTokenTiming(text, 0, 1)],
            });
    }
}
