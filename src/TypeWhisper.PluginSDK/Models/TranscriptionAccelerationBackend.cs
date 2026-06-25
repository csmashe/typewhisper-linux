// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     A concrete compute backend a transcription engine can resolve to.
/// </summary>
// ReSharper disable once UnusedType.Global
public enum TranscriptionAccelerationBackend
{
    // ReSharper disable once UnusedMember.Global
    Cpu,
    // ReSharper disable once UnusedMember.Global
    NvidiaCuda
}
