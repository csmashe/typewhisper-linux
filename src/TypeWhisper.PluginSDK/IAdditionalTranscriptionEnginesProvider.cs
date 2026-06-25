// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional capability expansion for plugins that expose additional transcription engine roles.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IAdditionalTranscriptionEnginesProvider
{
    /// <summary>Additional transcription engine roles exposed by this plugin.</summary>
    IReadOnlyList<ITranscriptionEnginePlugin> AdditionalTranscriptionEngines { get; }
}
