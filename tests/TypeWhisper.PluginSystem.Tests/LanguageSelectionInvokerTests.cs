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
    public async Task SupportedLanguageMatch_IsCaseInsensitiveButRequiresTheExactTag()
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

        Assert.Equal(1, role.CallCount);
        Assert.Equal("en", role.ReceivedLanguage);
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
    public async Task ExplicitUnsupported_ThrowsBeforeLegacyRoleIsInvoked()
    {
        var role = new RecordingRole(
            LanguageSelectionSupport.Supported,
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
