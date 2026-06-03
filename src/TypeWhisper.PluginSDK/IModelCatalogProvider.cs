namespace TypeWhisper.PluginSDK;

/// <summary>
///     Implemented by provider plugins whose list of available models is fetched
///     from a remote endpoint and can change at runtime (e.g. an
///     OpenAI-compatible server where the user pulls new models). Lets the UI
///     refresh the model catalog when a model dropdown opens.
///     <para>
///     Unlike <see cref="IPluginSettingsProvider.ValidateAsync" />, this MUST be
///     read-only with respect to user-visible side effects: it may perform a
///     network read to list models, but must not download assets, prompt for
///     license acceptance, mutate selections, or perform any
///     expensive/irreversible work. It is invoked by passive UI actions (opening
///     a dropdown), so anything heavier would surprise the user.
///     </para>
/// </summary>
public interface IModelCatalogProvider
{
    /// <summary>
    ///     Re-fetches the current model list from the provider and refreshes the
    ///     plugin's cached catalog (raising a capabilities change if it differs).
    ///     Should leave the cache untouched on failure rather than clearing it.
    /// </summary>
    Task RefreshModelCatalogAsync(CancellationToken ct = default);
}
