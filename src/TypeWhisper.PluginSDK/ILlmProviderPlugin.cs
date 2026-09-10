// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Owning plugin that provides an LLM role. The host manages lifecycle only through
///     <see cref="ITypeWhisperPlugin" /> and consumes LLM capabilities through
///     <see cref="ILlmProviderRole" />.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ILlmProviderPlugin : ITypeWhisperPlugin, ILlmProviderRole
{
    /// <summary>Unifies the plugin and role views of the owning plugin identifier.</summary>
    new string PluginId { get; }
}
