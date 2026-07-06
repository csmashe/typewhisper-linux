// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Describes a voice exposed by a text-to-speech provider.
/// </summary>
/// <param name="Id">Provider-specific voice identifier.</param>
/// <param name="DisplayName">Human-readable voice name for the UI.</param>
/// <param name="LocaleIdentifier">Optional locale identifier such as "en-US".</param>
// ReSharper disable once UnusedType.Global
public sealed record PluginVoiceInfo(
    string Id,
    string DisplayName,
    string? LocaleIdentifier = null
);
