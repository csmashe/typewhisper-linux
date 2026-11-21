using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LanguageSelectionInvokerTests
{
    [Fact]
    public async Task AutomaticSupported_InvokesLegacyAbiWithNull()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported
        );

        await role.TranscribeAsync(
            [],
            LanguageSelection.Automatic,
            false,
            null,
            CancellationToken.None
        );

        Assert.Equal(1, role.CallCount);
        Assert.Null(role.ReceivedLanguage);
    }

    [Fact]
    public async Task ExplicitSupported_InvokesLegacyAbiWithCanonicalTag()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported,
            ["zh-Hans-CN"]
        );
        Assert.True(LanguageSelection.TryParse(" zh-hANS-cn ", out var selection));

        await role.TranscribeAsync([], selection, false, null, CancellationToken.None);

        Assert.Equal(1, role.CallCount);
        Assert.Equal("zh-Hans-CN", role.ReceivedLanguage);
    }

    [Fact]
    public async Task ExplicitLanguageOutsideAdvertisedList_ThrowsBeforeLegacyRoleIsInvoked()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported,
            ["en", "de"]
        );

        var exception = await Assert.ThrowsAsync<TranscriptionLanguageNotSupportedException>(
            () =>
                role.TranscribeAsync(
                    [],
                    LanguageSelection.Explicit("fr"),
                    false,
                    null,
                    CancellationToken.None
                )
        );

        Assert.Equal("test", exception.ProviderId);
        Assert.Equal("model", exception.ModelId);
        Assert.Equal("fr", exception.Selection.LanguageTag);
        Assert.Equal(["en", "de"], exception.SupportedLanguages);
        Assert.Equal(0, role.CallCount);
    }

    [Fact]
    public async Task EmptySupportedLanguageList_PermitsEveryExplicitLanguage()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported,
            []
        );

        await role.TranscribeAsync(
            [],
            LanguageSelection.Explicit("ja-JP"),
            false,
            null,
            CancellationToken.None
        );

        Assert.Equal(1, role.CallCount);
        Assert.Equal("ja-JP", role.ReceivedLanguage);
    }

    [Fact]
    public async Task AutomaticSupport_IsIndependentOfExplicitLanguageList()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported,
            ["de"]
        );

        await role.TranscribeAsync(
            [],
            LanguageSelection.Automatic,
            false,
            null,
            CancellationToken.None
        );

        Assert.Equal(1, role.CallCount);
        Assert.Null(role.ReceivedLanguage);
    }

    [Fact]
    public async Task SupportedLanguageMatch_IsCaseInsensitiveAndFoldsRegionalTagsForBaseOnlyLists()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported,
            ["EN"]
        );

        await role.TranscribeAsync(
            [],
            LanguageSelection.Explicit("en"),
            false,
            null,
            CancellationToken.None
        );
        Assert.Equal(1, role.CallCount);
        Assert.Equal("en", role.ReceivedLanguage);

        await role.TranscribeAsync(
            [],
            LanguageSelection.Explicit("en-US"),
            false,
            null,
            CancellationToken.None
        );
        Assert.Equal(2, role.CallCount);
        Assert.Equal("en", role.ReceivedLanguage);
    }

    [Fact]
    public async Task SupportedLanguageMatch_StaysStrictWhenListHasRegionalTags()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported,
            ["en", "en-GB"]
        );

        await Assert.ThrowsAsync<TranscriptionLanguageNotSupportedException>(
            () =>
                role.TranscribeAsync(
                    [],
                    LanguageSelection.Explicit("en-US"),
                    false,
                    null,
                    CancellationToken.None
                )
        );

        Assert.Equal(0, role.CallCount);
    }

    [Fact]
    public async Task AutomaticUnsupported_ThrowsBeforeLegacyRoleIsInvoked()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Unsupported,
            LanguageSelectionSupport.Supported
        );

        var exception = await Assert.ThrowsAsync<LanguageSelectionNotSupportedException>(
            () =>
                role.TranscribeAsync(
                    [],
                    LanguageSelection.Automatic,
                    false,
                    null,
                    CancellationToken.None
                )
        );

        Assert.Equal("test", exception.ProviderId);
        Assert.Equal("model", exception.ModelId);
        Assert.Same(LanguageSelection.Automatic, exception.Selection);
        Assert.Equal(0, role.CallCount);
    }

    [Fact]
    public async Task ExplicitUnsupported_WithAutomaticDetection_FallsBackToAutomatic()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Unsupported
        );

        await role.TranscribeAsync(
            [],
            LanguageSelection.Explicit("de-DE"),
            false,
            null,
            CancellationToken.None
        );

        Assert.Equal(1, role.CallCount);
        Assert.Null(role.ReceivedLanguage);
    }

    [Fact]
    public void ResolveEffectiveSelection_ExplicitUnsupportedWithAutomaticDetection_IsAutomatic()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Unsupported
        );

        var effective = role.ResolveEffectiveSelection(LanguageSelection.Explicit("en"));

        Assert.True(effective.IsAutomatic);
        Assert.Null(effective.LanguageTag);
    }

    [Fact]
    public void ResolveEffectiveLanguage_DegradedWithLanguageList_DropsTheIgnoredTagFromHints()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Unsupported,
            ["en", "fr"]
        );

        var (selection, hints) = role.ResolveEffectiveLanguage(
            LanguageSelection.Explicit("de"),
            ["DE", "en"]
        );

        Assert.True(selection.IsAutomatic);
        Assert.Equal(["en"], hints);
    }

    [Fact]
    public async Task ResolveEffectiveLanguage_DegradedWithoutLanguageList_KeepsTheChoiceForTextProcessingOnly()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Unsupported
        );
        var selection = LanguageSelection.Explicit("de");
        IReadOnlyList<string> hints = ["de", "en"];

        var effective = role.ResolveEffectiveLanguage(selection, hints);
        await role.TranscribeAsync([], effective.Selection, effective.Hints, false, null, CancellationToken.None);

        Assert.Same(selection, effective.Selection);
        Assert.Same(hints, effective.Hints);
        Assert.Equal(1, role.CallCount);
        Assert.Null(role.ReceivedLanguage);
    }

    [Fact]
    public async Task KeptTextLanguage_ReachesAHintEngineOnlyAsTheOtherAdvisoryHints()
    {
        var role = new HintRecordingRole();
        var effective = role.ResolveEffectiveLanguage(LanguageSelection.Explicit("de"), ["de", "en"]);

        await role.TranscribeAsync([], effective.Selection, effective.Hints, false, null, CancellationToken.None);
        await using var session = await role.StartStreamingAsync(effective.Selection, effective.Hints, null, CancellationToken.None);

        Assert.Equal(0, role.CallCount);
        Assert.Equal<IReadOnlyList<string>>([["en"], ["en"]], role.ReceivedHints);
    }

    [Fact]
    public void ResolveEffectiveLanguage_NotDegraded_KeepsSelectionAndHints()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
            LanguageSelectionSupport.Supported
        );
        var selection = LanguageSelection.Explicit("de");
        IReadOnlyList<string> hints = ["de", "en"];

        var effective = role.ResolveEffectiveLanguage(selection, hints);

        Assert.Same(selection, effective.Selection);
        Assert.Same(hints, effective.Hints);
    }

    [Theory]
    [InlineData(LanguageSelectionSupport.Supported, LanguageSelectionSupport.Supported)]
    [InlineData(LanguageSelectionSupport.Unsupported, LanguageSelectionSupport.Unsupported)]
    [InlineData(LanguageSelectionSupport.Unknown, LanguageSelectionSupport.Unknown)]
    public void ResolveEffectiveSelection_OtherwiseKeepsTheSelection(
        LanguageSelectionSupport automatic,
        LanguageSelectionSupport explicitSupport
    )
    {
        var role = new RecordingRole(automatic, explicitSupport);
        var selection = LanguageSelection.Explicit("de-DE");

        Assert.Same(selection, role.ResolveEffectiveSelection(selection));
        Assert.Same(
            LanguageSelection.Automatic,
            role.ResolveEffectiveSelection(LanguageSelection.Automatic)
        );
    }

    [Fact]
    public async Task ExplicitUnsupported_WithoutAutomaticDetection_ThrowsBeforeLegacyRoleIsInvoked()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Unsupported,
            LanguageSelectionSupport.Unsupported
        );
        var selection = LanguageSelection.Explicit("de-DE");

        var exception = await Assert.ThrowsAsync<LanguageSelectionNotSupportedException>(
            () =>
                role.TranscribeAsync(
                    [],
                    selection,
                    false,
                    null,
                    CancellationToken.None
                )
        );

        Assert.Same(selection, exception.Selection);
        Assert.Equal(0, role.CallCount);
    }

    [Fact]
    public async Task UnknownCapability_PreservesLegacyAutomaticBehavior()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Unknown,
            LanguageSelectionSupport.Unknown
        );

        await role.TranscribeAsync(
            [],
            LanguageSelection.Automatic,
            false,
            null,
            CancellationToken.None
        );

        Assert.Equal(1, role.CallCount);
        Assert.Null(role.ReceivedLanguage);
    }

    [Fact]
    public async Task MissingCapabilityInterface_PreservesLegacyAutomaticBehavior()
    {
        var role = new LegacyRecordingRole();

        await role.TranscribeAsync(
            [],
            LanguageSelection.Automatic,
            false,
            null,
            CancellationToken.None
        );

        Assert.Equal(1, role.CallCount);
        Assert.Null(role.ReceivedLanguage);
    }

    private class LegacyRecordingRole : ITranscriptionEngineRole
    {
        public string PluginId => "test";
        public string ProviderId => "test";
        public string ProviderDisplayName => "Test";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("model", "Model")];
        // ReSharper disable once ReturnTypeCanBeNotNullable -- mirrors the nullable ITranscriptionEngineRole ABI, like every other role double in the suite.
        public string? SelectedModelId => "model";
        public bool SupportsTranslation => false;
        public int CallCount { get; private set; }
        public string? ReceivedLanguage { get; private set; }

        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            CallCount++;
            ReceivedLanguage = language;
            return Task.FromResult(new PluginTranscriptionResult("", null, 0, null));
        }
    }

    private sealed class HintRecordingRole
        : LegacyRecordingRole, ITranscriptionEngineRole, ITranscriptionLanguageSelectionCapabilities
    {
        public List<IReadOnlyList<string>> ReceivedHints { get; } = [];
        public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
        public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Unsupported;
        public bool SupportsLanguageHints => true;

        public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
            byte[] wavAudio,
            IReadOnlyList<string> languageHints,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            ReceivedHints.Add([.. languageHints]);
            return Task.FromResult(new PluginTranscriptionResult("", null, 0, null));
        }

        public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
            IReadOnlyList<string> languageHints,
            CancellationToken ct
        )
        {
            ReceivedHints.Add([.. languageHints]);
            return Task.FromResult<IStreamingSession>(new NullStreamingSession());
        }
    }

    private sealed class NullStreamingSession : IStreamingSession
    {
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public event Action<StreamingTranscriptEvent>? TranscriptReceived { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRole(
        LanguageSelectionSupport automaticSupport,
        LanguageSelectionSupport explicitSupport,
        IReadOnlyList<string>? supportedLanguages = null
    ) : LegacyRecordingRole, ITranscriptionEngineRole, ITranscriptionLanguageSelectionCapabilities
    {
        // ITranscriptionEngineRole is re-listed so interface mapping re-runs against this
        // class: without it, SupportedLanguages below would shadow the interface's default
        // implementation ([]) instead of implementing it, and the invoker would never see it.

        public LanguageSelectionSupport AutomaticDetectionSupport => automaticSupport;
        public LanguageSelectionSupport ExplicitSelectionSupport => explicitSupport;
        public IReadOnlyList<string> SupportedLanguages => supportedLanguages ?? [];
    }
}
