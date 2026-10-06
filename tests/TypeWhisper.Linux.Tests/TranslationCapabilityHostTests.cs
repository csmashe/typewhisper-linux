using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Vocabulary;
using TypeWhisper.Linux.Tests.Vocabulary;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Host handling of per-model translation capability: a translate task on a model that
///     declares it cannot translate is refused before audio decoding, model selection or
///     loading; undeclared models keep the legacy behavior of passing the task through.
/// </summary>
public sealed class TranslationCapabilityHostTests
{
    private static readonly string s_multilingual = TranslationCapabilityEngine.FullModelId("multilingual");
    private static readonly string s_englishOnly = TranslationCapabilityEngine.FullModelId("english-only");
    private static readonly string s_legacy = TranslationCapabilityEngine.FullModelId("legacy");

    [Fact]
    public void GetTranslationRejection_ModelDeclaresUnsupported_RefusesWithoutTouchingTheEngine()
    {
        var engine = new TranslationCapabilityEngine("multilingual");
        using var models = CreateModels(engine, new AppSettings());

        Assert.NotNull(models.GetTranslationRejection(s_englishOnly));
        Assert.Equal((0, 0, 0), (engine.LoadCount, engine.SelectCount, engine.TranscribeCount));
        Assert.Equal("multilingual", engine.SelectedModelId);
    }

    [Fact]
    public void GetTranslationRejection_SupportedUndeclaredOrUnknownModels_AreNotRefused()
    {
        using var models = CreateModels(new TranslationCapabilityEngine("english-only"), new AppSettings());

        Assert.Null(models.GetTranslationRejection(s_multilingual));
        Assert.Null(models.GetTranslationRejection(s_legacy));
        Assert.Null(models.GetTranslationRejection(TranslationCapabilityEngine.FullModelId("missing")));
        Assert.Null(models.GetTranslationRejection(ModelManagerService.GetPluginModelId("other", "english-only")));
        Assert.Null(models.GetTranslationRejection("not-a-plugin-model"));
        Assert.Null(models.GetTranslationRejection(null));
    }

    [Fact]
    public void SupportsTranslation_UsesTheModelDeclarationBeforeTheEngineFlag()
    {
        // The engine's flag follows its selected model (multilingual → true), but each
        // declared model answers for itself; the undeclared model inherits the engine flag.
        using var models = CreateModels(new TranslationCapabilityEngine("multilingual"), new AppSettings());

        Assert.True(models.SupportsTranslation(s_multilingual));
        Assert.False(models.SupportsTranslation(s_englishOnly));
        Assert.True(models.SupportsTranslation(s_legacy));
        Assert.False(models.SupportsTranslation(null));
    }

    [Theory]
    [InlineData(null, "translate", "english-only", true)]
    [InlineData(null, "translate", "multilingual", false)]
    [InlineData(null, "translate", "legacy", false)]
    [InlineData(null, "transcribe", "english-only", false)]
    [InlineData("translate", "transcribe", "english-only", true)]
    [InlineData("transcribe", "translate", "english-only", false)]
    public void DescribeTranslationRejection_UsesTheProfileTaskElseTheGlobalTask(
        string? profileTask, string globalTask, string globalModel, bool refused)
    {
        var settings = new AppSettings
        {
            TranscriptionTask = globalTask,
            SelectedModelId = TranslationCapabilityEngine.FullModelId(globalModel),
        };
        using var models = CreateModels(new TranslationCapabilityEngine("multilingual"), settings);
        var profile = profileTask is null ? null : new Profile { Id = "p", Name = "P", SelectedTask = profileTask };

        Assert.Equal(refused, DictationOrchestrator.DescribeTranslationRejection(profile, settings, models) is not null);
    }

    [Fact]
    public void DescribeTranslationRejection_ChecksTheProfileModelOverride()
    {
        var settings = new AppSettings { TranscriptionTask = "translate", SelectedModelId = s_multilingual };
        using var models = CreateModels(new TranslationCapabilityEngine("multilingual"), settings);
        var profile = new Profile { Id = "p", Name = "P", TranscriptionModelOverride = s_englishOnly };

        Assert.NotNull(DictationOrchestrator.DescribeTranslationRejection(profile, settings, models));
        Assert.Null(DictationOrchestrator.DescribeTranslationRejection(
            profile with { TranscriptionModelOverride = s_multilingual }, settings, models));
    }

    [Fact]
    public async Task FileTranscription_TranslateOnModelThatCannotTranslate_RefusesBeforeDecodingOrLoading()
    {
        var engine = new TranslationCapabilityEngine("multilingual");
        await using var harness = new FileHarness(engine, s_englishOnly);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ProcessAsync(TranscriptionTask.Translate));

        Assert.Equal(harness.Models.GetTranslationRejection(s_englishOnly), ex.Message);
        Assert.Empty(harness.Runner.SupervisorInvocations);
        Assert.Equal((0, 0, 0), (engine.LoadCount, engine.SelectCount, engine.TranscribeCount));
    }

    [Fact]
    public async Task FileTranscription_GlobalTranslateTask_IsRefusedForTheSameModel()
    {
        var engine = new TranslationCapabilityEngine("multilingual");
        await using var harness = new FileHarness(engine, s_englishOnly, globalTask: "translate");

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ProcessAsync(task: null));

        Assert.Equal(0, engine.TranscribeCount);
    }

    [Theory]
    [InlineData("english-only", TranscriptionTask.Transcribe, false)]
    [InlineData("multilingual", TranscriptionTask.Translate, true)]
    [InlineData("legacy", TranscriptionTask.Translate, true)]
    public async Task FileTranscription_AllowedTasks_ReachTheEngineUnchanged(
        string modelId, TranscriptionTask task, bool expectedTranslate)
    {
        var engine = new TranslationCapabilityEngine("multilingual");
        await using var harness = new FileHarness(engine, TranslationCapabilityEngine.FullModelId(modelId));

        var result = await harness.ProcessAsync(task);

        Assert.Equal("transcribed", result.ProcessedText);
        Assert.Equal(1, engine.TranscribeCount);
        Assert.Equal(expectedTranslate, engine.LastTranslate);
    }

    private static ModelManagerService CreateModels(TranslationCapabilityEngine engine, AppSettings settings) =>
        new(
            TestPluginManagerFactory.Create(transcriptionEngines: [engine]),
            TestPluginManagerFactory.CreateSettings(settings).Object
        );

    private sealed class FileHarness : IAsyncDisposable
    {
        private readonly string _path = TestPaths.NewTempPath("translation-capability.wav");
        private readonly FileTranscriptionProcessor _processor;
        private readonly PluginManager _plugins;

        public FileHarness(TranslationCapabilityEngine engine, string modelId, string globalTask = "transcribe")
        {
            _plugins = TestPluginManagerFactory.Create(transcriptionEngines: [engine]);
            var plugins = _plugins;
            var settings = TestPluginManagerFactory.CreateSettings(
                new AppSettings { SelectedModelId = modelId, TranscriptionTask = globalTask });
            var dictionary = new Mock<IDictionaryService>();
            dictionary.SetupGet(d => d.Entries).Returns([]);
            var pipeline = new Mock<IPostProcessingPipeline>();
            pipeline
                .Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<PipelineOptions>(), It.IsAny<CancellationToken>()))
                .Returns((string text, PipelineOptions _, CancellationToken _) =>
                    Task.FromResult(new PostProcessingResult { Text = text }));
            Runner = new FakeProcessRunner
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
            var commands = new SystemCommandAvailabilityService(Runner);
            commands.RaiseSnapshotChangedForTests(commands.GetSnapshot() with { HasFfmpeg = true });
            Models = new ModelManagerService(plugins, settings.Object);
            _processor = new FileTranscriptionProcessor(
                Models,
                settings.Object,
                new AudioFileService(commands, Runner),
                dictionary.Object,
                Mock.Of<IVocabularyBoostingService>(),
                new VocabularyRescoringService(plugins, settings.Object, dictionary.Object, Models),
                pipeline.Object
            );
            File.WriteAllBytes(_path, [0]);
            Runner.SupervisorInvocations.Clear();
        }

        public ModelManagerService Models { get; }

        public FakeProcessRunner Runner { get; }

        public Task<FileTranscriptionProcessResult> ProcessAsync(TranscriptionTask? task) =>
            _processor.ProcessAsync(_path, _ => { }, new FileTranscriptionProcessOptions(Task: task), CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            Models.Dispose();
            _plugins.Dispose();
            File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }
}
