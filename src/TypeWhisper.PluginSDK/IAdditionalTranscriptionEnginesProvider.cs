// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional capability expansion for plugins that expose additional transcription-engine
///     roles. The parent plugin owns every returned role's lifetime; the host never activates,
///     deactivates, or disposes returned objects.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IAdditionalTranscriptionEnginesProvider
{
    /// <summary>
    ///     Additional transcription-engine roles exposed by this plugin. Returned role
    ///     instances MUST be stable across calls so capability-index rebuilds reuse the
    ///     same objects.
    /// </summary>
    IReadOnlyList<ITranscriptionEngineRole> AdditionalTranscriptionEngines { get; }
}
