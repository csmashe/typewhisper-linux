// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Base interface for all TypeWhisper plugins. The host calls <c>ActivateAsync</c> once
///     after construction and <c>DeactivateAsync</c> before <c>Dispose</c>. Neither method
///     may block — long-running work must run on a background task.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ITypeWhisperPlugin : IDisposable
{
    /// <summary>Unique identifier for the plugin (e.g. "com.example.my-plugin").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string PluginId { get; }

    /// <summary>Human-readable display name.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string PluginName { get; }

    /// <summary>Semantic version string (e.g. "1.0.0").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string PluginVersion { get; }

    Task ActivateAsync(IPluginHostServices host);

    Task DeactivateAsync();
}
