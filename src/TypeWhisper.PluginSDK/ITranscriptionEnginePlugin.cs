// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Owning plugin that provides a transcription-engine role. The host manages lifecycle
///     only through <see cref="ITypeWhisperPlugin" /> and consumes transcription capabilities
///     through <see cref="ITranscriptionEngineRole" />.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ITranscriptionEnginePlugin : ITypeWhisperPlugin, ITranscriptionEngineRole
{
    /// <summary>Unifies the plugin and role views of the owning plugin identifier.</summary>
    // ReSharper disable once UnusedMemberInSuper.Global -- consumed by out-of-solution plugins/host through this owning-plugin view; no in-solution caller is visible.
    new string PluginId { get; }
}
