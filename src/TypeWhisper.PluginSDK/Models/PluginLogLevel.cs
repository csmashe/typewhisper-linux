// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>Log severity level passed to <see cref="IPluginHostServices.Log" />.</summary>
// ReSharper disable once UnusedType.Global
public enum PluginLogLevel
{
    // ReSharper disable once UnusedMember.Global
    Debug,
    // ReSharper disable once UnusedMember.Global
    Info,
    // ReSharper disable once UnusedMember.Global
    Warning,
    // ReSharper disable once UnusedMember.Global
    Error
}
