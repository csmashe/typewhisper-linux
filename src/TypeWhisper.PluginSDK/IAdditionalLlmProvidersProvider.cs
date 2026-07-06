// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional capability expansion for plugins that expose additional LLM provider roles.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IAdditionalLlmProvidersProvider
{
    /// <summary>Additional LLM provider roles exposed by this plugin.</summary>
    IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders { get; }
}
