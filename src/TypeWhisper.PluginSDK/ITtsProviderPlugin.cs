// ReSharper disable UnusedMemberInSuper.Global
// PluginSDK contract members are implemented by out-of-solution plugin projects and invoked by
// the host; the analyzer sees no in-solution caller, so these .Global inspections misfire.

// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides text-to-speech playback for spoken feedback and readback.
/// </summary>
/// <remarks>
///     Async members use the SDK cancellation-origin contract: success uses the existing return;
///     caller cancellation throws <see cref="OperationCanceledException" /> only when the supplied
///     token is requested; private deadlines throw <see cref="TimeoutException" /> (or a
///     provider-specific subclass); every other exception, including an OCE while the supplied
///     token is live, is a dependency fault. At catch time caller cancellation wins over a private
///     timeout, which wins over a dependency fault; if both tokens are requested, caller wins.
/// </remarks>
// ReSharper disable once UnusedType.Global
public interface ITtsProviderPlugin : ITypeWhisperPlugin
{
    /// <summary>Unique provider identifier.</summary>
    string ProviderId { get; }

    /// <summary>Human-readable provider name for the UI.</summary>
    string ProviderDisplayName { get; }

    /// <summary>Whether the provider is configured and ready to speak.</summary>
    bool IsConfigured { get; }

    /// <summary>Available voices for this provider.</summary>
    IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; }

    /// <summary>Currently selected voice ID, or null for the provider default.</summary>
    string? SelectedVoiceId { get; }

    /// <summary>Optional summary of current provider-specific settings.</summary>
    // ReSharper disable once UnusedMember.Global
    string? SettingsSummary => null;

    /// <summary>Selects a voice by provider-specific ID, or null for the provider default.</summary>
    void SelectVoice(string? voiceId);

    /// <summary>Speaks the requested text and returns a playback session that can be stopped.</summary>
    Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct);
}
