// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Text-to-speech playback request passed from the host to a TTS provider plugin.
/// </summary>
/// <param name="Text">Text to speak.</param>
/// <param name="Language">Optional BCP-47/ISO language hint.</param>
/// <param name="Purpose">Why the host is requesting playback.</param>
// ReSharper disable once UnusedType.Global
public sealed record TtsSpeakRequest(
    string Text,
    string? Language = null,
    TtsPurpose Purpose = TtsPurpose.Status
);
