// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional stable selection identity for LLM provider roles.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ILlmProviderSelectionIdentity
{
    /// <summary>Stable identifier used in plugin LLM selection IDs.</summary>
    string LlmSelectionId { get; }
}
