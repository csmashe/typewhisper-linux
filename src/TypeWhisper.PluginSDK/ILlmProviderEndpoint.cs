// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional for LLM provider roles whose server the user configures. Reports the base URL the
///     role sends requests to, so the host can recognize a server running on this computer.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ILlmProviderEndpoint
{
    /// <summary>
    ///     The configured base URL as it is currently used for requests, or null when none is set.
    ///     Read again before each use: the user can change it at any time.
    /// </summary>
    Uri? LlmEndpoint { get; }
}
