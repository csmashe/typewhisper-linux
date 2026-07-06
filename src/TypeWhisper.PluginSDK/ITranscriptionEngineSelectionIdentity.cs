// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional stable selection identity for transcription engine roles.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ITranscriptionEngineSelectionIdentity
{
    /// <summary>Stable identifier used in plugin model selection IDs.</summary>
    string TranscriptionSelectionId { get; }
}
