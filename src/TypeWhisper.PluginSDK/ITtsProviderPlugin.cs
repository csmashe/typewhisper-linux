// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides text-to-speech playback for spoken feedback and readback.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ITtsProviderPlugin : ITypeWhisperPlugin
{
    /// <summary>Unique provider identifier.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string ProviderId { get; }

    /// <summary>Human-readable provider name for the UI.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string ProviderDisplayName { get; }

    /// <summary>Whether the provider is configured and ready to speak.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    bool IsConfigured { get; }

    /// <summary>Available voices for this provider.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; }

    /// <summary>Currently selected voice ID, or null for the provider default.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string? SelectedVoiceId { get; }

    /// <summary>Optional summary of current provider-specific settings.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string? SettingsSummary => null;

    /// <summary>Selects a voice by provider-specific ID, or null for the provider default.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    void SelectVoice(string? voiceId);

    /// <summary>Speaks the requested text and returns a playback session that can be stopped.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct);
}
