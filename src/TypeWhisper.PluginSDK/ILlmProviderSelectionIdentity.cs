// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional stable selection identity for LLM provider roles. Selection IDs must contain
///     only ASCII letters, ASCII digits, dots, dashes, and underscores
///     (<c>[A-Za-z0-9._-]+</c>).
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ILlmProviderSelectionIdentity
{
    /// <summary>
    ///     Stable identifier used in plugin LLM selection IDs. A null, empty, or
    ///     whitespace-only value is treated as absent and falls back to the plugin ID;
    ///     the resulting effective ID must match <c>[A-Za-z0-9._-]+</c>.
    /// </summary>
    string LlmSelectionId { get; }
}
