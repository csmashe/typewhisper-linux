using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Rewrites saved LLM selections ("plugin:&lt;id&gt;:&lt;model&gt;") whose model a provider now lists
///     under another ID, such as an alias or a retired model, so pickers show the model in use.
///     Selections the provider cannot resolve stay as saved.
/// </summary>
internal static class LlmSelectionMigration
{
    /// <summary>
    ///     Rewrites the default and spoken-command selections. Prompt actions keep their saved
    ///     value, since rewriting one reloads an open editor and drops its draft; the editor
    ///     resolves them with <see cref="Migrate" /> instead.
    /// </summary>
    public static void Apply(IReadOnlyList<ILlmProviderRole> providers, ISettingsService settings)
    {
        var current = settings.Current;
        var defaultProvider = Migrate(current.DefaultLlmProvider, providers);
        var spokenCommandProvider = Migrate(current.SpokenCommandLlmProvider, providers);
        if (string.Equals(defaultProvider, current.DefaultLlmProvider, StringComparison.Ordinal)
            && string.Equals(spokenCommandProvider, current.SpokenCommandLlmProvider, StringComparison.Ordinal))
        {
            return;
        }

        settings.Update(s => s with
        {
            DefaultLlmProvider = Migrate(s.DefaultLlmProvider, providers),
            SpokenCommandLlmProvider = Migrate(s.SpokenCommandLlmProvider, providers),
        });
    }

    /// <summary>The selection naming the listed model, or <paramref name="selection" /> unchanged.</summary>
    internal static string? Migrate(string? selection, IReadOnlyList<ILlmProviderRole> providers)
    {
        var parts = selection?.Split(':', 3);
        if (parts is not ["plugin", var selectionId, var modelId] || string.IsNullOrWhiteSpace(modelId))
            return selection;

        var provider = providers.FirstOrDefault(candidate => candidate.GetLlmSelectionId() == selectionId);
        if (provider is null || provider.SupportedModels.Any(model => model.Id == modelId))
            return selection;

        return provider.ResolveSavedModelId(modelId) is { } resolved
               && provider.SupportedModels.Any(model => model.Id == resolved)
            ? $"plugin:{selectionId}:{resolved}"
            : selection;
    }
}
