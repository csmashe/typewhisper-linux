// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Implemented by provider plugins whose model list is fetched from a remote endpoint and can
///     change at runtime (e.g. an OpenAI-compatible server). Lets the UI refresh the catalog when a
///     model dropdown opens. MUST be read-only: network reads to list models are fine, but no
///     asset downloads, license prompts, selection mutations, or expensive/irreversible work —
///     it is invoked by passive UI actions.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IModelCatalogProvider
{
    /// <summary>
    ///     Re-fetches the model list and refreshes the plugin's cached catalog (raising a capabilities
    ///     change if it differs). Leave the cache untouched on failure rather than clearing it.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    Task RefreshModelCatalogAsync(CancellationToken ct = default);
}
