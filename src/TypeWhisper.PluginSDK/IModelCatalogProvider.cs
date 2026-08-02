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
/// <remarks>
///     Async members use the SDK cancellation-origin contract: success uses the existing return;
///     caller cancellation throws <see cref="OperationCanceledException" /> only when the supplied
///     token is requested; private deadlines throw <see cref="TimeoutException" /> (or a
///     provider-specific subclass); every other exception, including an OCE while the supplied
///     token is live, is a dependency fault. At catch time caller cancellation wins over a private
///     timeout, which wins over a dependency fault; if both tokens are requested, caller wins.
/// </remarks>
// ReSharper disable once UnusedType.Global
public interface IModelCatalogProvider
{
    /// <summary>
    ///     Re-fetches the model list and refreshes the plugin's cached catalog (raising a capabilities
    ///     change if it differs). Leave the cache untouched on failure rather than clearing it.
    /// </summary>
    Task RefreshModelCatalogAsync(CancellationToken ct = default);
}
