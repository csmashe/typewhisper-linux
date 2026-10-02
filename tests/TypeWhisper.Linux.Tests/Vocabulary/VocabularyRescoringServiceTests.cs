using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Vocabulary;
using TypeWhisper.PluginSDK;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests.Vocabulary;

public sealed class VocabularyRescoringServiceTests : IDisposable
{
    private readonly PluginManager _plugins = TestPluginManagerFactory.Create();
    private readonly Mock<ISettingsService> _settings = TestPluginManagerFactory.CreateSettings(
        new AppSettings()
    );
    private readonly Mock<IDictionaryService> _dictionary = new();
    private readonly FakeVocabularyRescorerPlugin _plugin = new();
    private readonly VocabularyRescoringService _service;
    private readonly ModelManagerService _models;
    private readonly Mock<ITranscriptionEngineRole> _engine = new();
    private static readonly string[] s_distinctTerms = ["TypeWhisper", "typewhisper"];
    private static readonly string[] s_aliasAndCanonical = ["type wisper", "TypeWhisper"];

    public VocabularyRescoringServiceTests()
    {
        _engine.SetupGet(e => e.PluginId).Returns("test-sherpa");
        _engine.SetupGet(e => e.ProviderId).Returns("sherpa-onnx");
        _engine.SetupGet(e => e.SelectedModelId).Returns("parakeet-tdt-0.6b");
        PluginManagerTestAccess.SetTranscriptionEngines(_plugins, [_engine.Object]);
        _models = new ModelManagerService(_plugins, _settings.Object);
        typeof(ModelManagerService)
            .GetProperty(nameof(ModelManagerService.ActiveModelId))!
            .SetValue(
                _models,
                ModelManagerService.GetPluginModelId("test-sherpa", "parakeet-tdt-0.6b")
            );
        _dictionary.SetupGet(d => d.Entries).Returns([Term("TypeWhisper")]);
        PluginManagerTestAccess.SetVocabularyRescorers(_plugins, [_plugin]);
        _service = new VocabularyRescoringService(
            _plugins,
            _settings.Object,
            _dictionary.Object,
            _models,
            TimeSpan.FromMilliseconds(100)
        );
    }

    private static DictionaryEntry Term(
        string text,
        bool enabled = true,
        DictionaryEntryType type = DictionaryEntryType.Term,
        string? replacement = null
    ) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Original = text,
            Replacement = replacement,
            IsEnabled = enabled,
            EntryType = type,
        };

    private static VocabularyRescoringInput Input() =>
        new(
            Guid.NewGuid(),
            "type whisper",
            PcmWavTests.CreateWav([0, 123, -123]),
            [new VocabularyTokenTiming("type whisper", 0, 1)],
            false,
            "sherpa-onnx",
            "parakeet-tdt-0.6b"
        );

    private static bool IsEligible(
        VocabularyRescoringService service,
        VocabularyRescoringInput input
    ) =>
        service.IsEligible(
            input.TokenTimings,
            input.TranslateRequested,
            input.EngineProviderId,
            input.EngineModelId
        );

    [Theory]
    [InlineData("engine")]
    [InlineData("model")]
    [InlineData("setting")]
    [InlineData("translate")]
    [InlineData("timings")]
    [InlineData("plugin")]
    [InlineData("ready")]
    [InlineData("terms")]
    public async Task Ineligible_NeverCallsPlugin(string reason)
    {
        var input = Input();
        switch (reason)
        {
            case "engine":
                input = input with { EngineProviderId = "whisper-cpp" };
                break;
            case "model":
                input = input with { EngineModelId = "canary-180m-flash" };
                break;
            case "setting":
                _settings.Object.Update(s => s with { AcousticVocabularyBoostingEnabled = false });
                break;
            case "translate":
                input = input with { TranslateRequested = true };
                break;
            case "timings":
                input = input with { TokenTimings = [] };
                break;
            case "plugin":
                PluginManagerTestAccess.SetVocabularyRescorers(_plugins, []);
                break;
            case "ready":
                _plugin.IsReady = false;
                break;
            case "terms":
                _dictionary
                    .SetupGet(d => d.Entries)
                    .Returns([
                        Term("disabled", false),
                        Term("correction", type: DictionaryEntryType.Correction),
                    ]);
                break;
        }
        Assert.False(IsEligible(_service, input));
        var result = await _service.RefineAsync(input, CancellationToken.None);
        Assert.Equal(new VocabularyRescoringOutcome(input.Text, false, false, null), result);
        Assert.Equal(0, _plugin.CallCount);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("model")]
    [InlineData("inactive")]
    public async Task LoadedModelOnlyDrivesStatus_ParakeetTranscriptStaysEligible(string reason)
    {
        Assert.Null(_service.ActiveEngineBlocker);
        switch (reason)
        {
            case "engine":
                _engine.SetupGet(e => e.ProviderId).Returns("whisper-cpp");
                break;
            case "model":
                _engine.SetupGet(e => e.SelectedModelId).Returns("canary-180m-flash");
                break;
            case "inactive":
                typeof(ModelManagerService)
                    .GetProperty(nameof(ModelManagerService.ActiveModelId))!
                    .SetValue(_models, null);
                break;
        }
        // An HTTP request may load another model after dictation released its lease.
        Assert.Equal("engine", _service.ActiveEngineBlocker);
        var input = Input();
        Assert.True(IsEligible(_service, input));
        var result = await _service.RefineAsync(input, CancellationToken.None);
        Assert.Equal(new VocabularyRescoringOutcome("TypeWhisper", true, true, null), result);
        Assert.Equal(1, _plugin.CallCount);
    }

    [Fact]
    public async Task InvalidWav_IsNotEligible()
    {
        var input = Input() with { Wav = [1, 2, 3] };
        Assert.Equal(
            new VocabularyRescoringOutcome(input.Text, false, false, null),
            await _service.RefineAsync(input, CancellationToken.None)
        );
        Assert.Equal(0, _plugin.CallCount);
    }

    [Fact]
    public async Task Eligible_CopiesSnapshotsAndAppliesValidatedReplacement()
    {
        _dictionary
            .SetupGet(d => d.Entries)
            .Returns([
                Term("TypeWhisper"),
                Term("TypeWhisper"),
                Term("typewhisper"),
                Term("disabled", false),
                Term("correction", type: DictionaryEntryType.Correction),
            ]);
        var timings = new List<VocabularyTokenTiming> { new("type whisper", 0, 1) };
        var input = Input() with { TokenTimings = timings };
        var result = await _service.RefineAsync(input, CancellationToken.None);
        timings.Clear();
        var request = Assert.IsType<VocabularyRescoreRequest>(_plugin.Request);
        Assert.Equal(input.RecordingId, request.RecordingId);
        Assert.Equal(16000, request.SampleRate);
        Assert.Equal(3, request.Audio.Length);
        Assert.Single(request.TokenTimings);
        Assert.Equal(s_distinctTerms, request.Terms.Select(t => t.Text));
        Assert.All(request.Terms, term => Assert.Null(term.MinimumSimilarity));
        Assert.Equal(new VocabularyRescoringOutcome("TypeWhisper", true, true, null), result);
    }

    [Fact]
    public async Task HintsCarryThresholdForAliasAndCanonicalAndNullWhenUnset()
    {
        _dictionary
            .SetupGet(d => d.Entries)
            .Returns([
                Term("type wisper", replacement: "TypeWhisper") with
                {
                    CtcMinSimilarity = .65f,
                },
                Term("auto"),
            ]);
        Assert.Null(_service.ActiveEngineBlocker);
        await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Collection(
            _plugin.Request!.Terms,
            hint => Assert.Equal(.65f, hint.MinimumSimilarity),
            hint => Assert.Equal(.65f, hint.MinimumSimilarity),
            hint => Assert.Null(hint.MinimumSimilarity)
        );
    }

    [Fact]
    public async Task TermWithReplacement_ScoresAliasAndWritesCanonicalSpelling()
    {
        _dictionary
            .SetupGet(d => d.Entries)
            .Returns([Term("type wisper", replacement: " TypeWhisper ")]);
        _plugin.Handler = (request, _) =>
            Task.FromResult(
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(0, request.Text.Length, request.Terms[0].Text, 1)]
                )
            );
        var result = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal(s_aliasAndCanonical, _plugin.Request!.Terms.Select(t => t.Text));
        Assert.Equal(new VocabularyRescoringOutcome("TypeWhisper", true, true, null), result);
    }

    [Fact]
    public async Task ManualReplacement_OutranksEarlierPackAlias()
    {
        _dictionary
            .SetupGet(d => d.Entries)
            .Returns([
                Term("type wisper") with { Id = "pack:test:type wisper" },
                Term("type wisper", replacement: "TypeWhisper"),
            ]);
        _plugin.Handler = (request, _) =>
            Task.FromResult(
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(0, request.Text.Length, request.Terms[0].Text, 1)]
                )
            );
        var result = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal(new VocabularyRescoringOutcome("TypeWhisper", true, true, null), result);
    }

    [Fact]
    public async Task UnknownTerm_KeepsOriginal()
    {
        _plugin.Handler = (request, _) =>
            Task.FromResult(
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(0, request.Text.Length, "Untrusted", 1)]
                )
            );
        var result = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal(
            new VocabularyRescoringOutcome(
                "type whisper",
                false,
                false,
                "Vocabulary rescoring failed: InvalidDataException"
            ),
            result
        );
    }

    [Fact]
    public async Task WrongRecordingId_KeepsOriginal()
    {
        _plugin.Handler = (_, _) =>
            Task.FromResult(new VocabularyRescoreResult(Guid.NewGuid(), []));
        var result = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal("type whisper", result.Text);
        Assert.False(result.Applied);
        Assert.Equal("Vocabulary rescoring failed: InvalidDataException", result.Error);
    }

    [Fact]
    public async Task PluginException_OnlyReportsType()
    {
        _plugin.Handler = (_, _) =>
            throw new InvalidOperationException("private transcript and terms");
        var result = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal("type whisper", result.Text);
        Assert.False(result.Eligible);
        Assert.False(result.Applied);
        Assert.Equal("Vocabulary rescoring failed: InvalidOperationException", result.Error);
    }

    [Fact]
    public async Task PluginWithdrawsReadinessWhileFailing_ReportsIneligible()
    {
        _plugin.Handler = (_, _) =>
        {
            _plugin.IsReady = false;
            throw new FileNotFoundException("model");
        };
        var result = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal(
            new VocabularyRescoringOutcome(
                "type whisper",
                false,
                false,
                "Vocabulary rescoring failed: FileNotFoundException"
            ),
            result
        );
    }

    [Fact]
    public async Task UncooperativePlugin_TimesOutAndCancelsToken()
    {
        var never = new TaskCompletionSource<VocabularyRescoreResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _plugin.Handler = (_, _) => never.Task;
        var result = await _service
            .RefineAsync(Input(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("type whisper", result.Text);
        Assert.False(result.Eligible);
        Assert.False(result.Applied);
        Assert.Equal("Vocabulary rescoring timed out after 0.1 s", result.Error);
        Assert.True(_plugin.Token.IsCancellationRequested);
        // A timed-out decoder still excludes later calls until it drains, but they must
        // return at once instead of waiting out another timeout.
        var skipped = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal(
            new VocabularyRescoringOutcome(
                "type whisper",
                false,
                false,
                "Vocabulary rescoring skipped: previous call still running"
            ),
            skipped
        );
        Assert.Equal(1, _plugin.CallCount);
        never.SetResult(new VocabularyRescoreResult(_plugin.Request!.RecordingId, []));
        _plugin.Handler = (request, _) =>
            Task.FromResult(
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(0, request.Text.Length, "TypeWhisper", 1)]
                )
            );
        // Once the stale call drains, the next call reaches the plugin again.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        VocabularyRescoringOutcome resumed;
        do
        {
            await Task.Delay(10);
            resumed = await _service.RefineAsync(Input(), CancellationToken.None);
        } while (resumed.Error is not null && DateTime.UtcNow < deadline);
        Assert.True(resumed.Applied);
        Assert.Equal(2, _plugin.CallCount);
    }

    [Fact]
    public async Task BlockingPlugin_TimesOutWithoutStallingCaller()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _plugin.Handler = (request, _) =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the handler runs inside the awaited RefineAsync; the drain loop below only ends once it returned.
            started.Set();
            // ReSharper disable once AccessToDisposedClosure -- the handler runs inside the awaited RefineAsync; the drain loop below only ends once it returned.
            release.Wait(CancellationToken.None);
            return Task.FromResult(new VocabularyRescoreResult(request.RecordingId, []));
        };
        var result = await _service
            .RefineAsync(Input(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Vocabulary rescoring timed out after 0.1 s", result.Error);
        Assert.False(result.Applied);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(_plugin.Token.IsCancellationRequested);
        var skipped = await _service
            .RefineAsync(Input(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            "Vocabulary rescoring skipped: previous call still running",
            skipped.Error
        );
        Assert.Equal(1, _plugin.CallCount);
        release.Set();
        _plugin.Handler = (request, _) =>
            Task.FromResult(
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(0, request.Text.Length, "TypeWhisper", 1)]
                )
            );
        var deadline = DateTime.UtcNow.AddSeconds(5);
        VocabularyRescoringOutcome resumed;
        do
        {
            await Task.Delay(10);
            resumed = await _service.RefineAsync(Input(), CancellationToken.None);
        } while (resumed.Error is not null && DateTime.UtcNow < deadline);
        Assert.True(resumed.Applied);
        Assert.Equal(2, _plugin.CallCount);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        _plugin.Handler = async (request, token) =>
        {
            // ReSharper disable once AccessToDisposedClosure -- runs before the token is cancelled, so it precedes the awaited RefineAsync's return.
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return new VocabularyRescoreResult(request.RecordingId, []);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.RefineAsync(Input(), cancellation.Token)
        );
        Assert.True(_plugin.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginUnavailableDuringCall_DiscardsResult(bool removed)
    {
        _plugin.Handler = (request, _) =>
        {
            if (removed)
                PluginManagerTestAccess.SetVocabularyRescorers(_plugins, []);
            else
                _plugin.IsReady = false;
            return Task.FromResult(
                new VocabularyRescoreResult(
                    request.RecordingId,
                    [new VocabularyReplacement(0, request.Text.Length, "TypeWhisper", 1)]
                )
            );
        };
        var result = await _service.RefineAsync(Input(), CancellationToken.None);
        Assert.Equal(
            new VocabularyRescoringOutcome(
                "type whisper",
                false,
                false,
                "Vocabulary rescoring result discarded: plugin unavailable"
            ),
            result
        );
    }

    public void Dispose()
    {
        _models.Dispose();
        _plugins.Dispose();
    }
}
