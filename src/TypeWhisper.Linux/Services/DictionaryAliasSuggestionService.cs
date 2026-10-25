using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>A ready model of a local LLM role that may generate alias suggestions.</summary>
public sealed record LocalAliasModel(string ProviderId, string ModelId, string DisplayName);

/// <summary>
///     Generates alias suggestions with local LLMs only, never a cloud provider: a plugin that runs
///     locally, or a configurable server whose endpoint is a loopback address. The role is
///     re-resolved and re-checked on every request.
/// </summary>
public sealed class DictionaryAliasSuggestionService(PluginManager pluginManager)
{
    public IReadOnlyList<LocalAliasModel> GetLocalModels()
    {
        var models = new List<LocalAliasModel>();
        foreach (var provider in pluginManager.LlmProviders)
        {
            try
            {
                if (!IsLocal(provider) || !provider.IsAvailable)
                {
                    continue;
                }

                var providerId = provider.GetLlmSelectionId();
                models.AddRange(LocalModels(provider).Select(model => new LocalAliasModel(
                    providerId,
                    model.Id,
                    $"{provider.ProviderName} · {model.DisplayName}")));
            }
            catch (Exception ex)
            {
                // A misbehaving third-party role must not hide the others.
                System.Diagnostics.Trace.WriteLine(
                    $"[DictionaryAliasSuggestionService] Skipping LLM role: {ex.Message}");
            }
        }

        return models;
    }

    /// <summary>Asks the chosen local model for spoken variants of <paramref name="term"/>.</summary>
    /// <exception cref="InvalidOperationException">The model is no longer local, available and loaded.</exception>
    /// <exception cref="FormatException">The model did not return a usable list.</exception>
    public Task<IReadOnlyList<string>> SuggestAsync(
        string term,
        string spokenLanguage,
        LocalAliasModel model,
        CancellationToken ct
    )
    {
        return DictionaryAliasSuggestions.GenerateAsync(term, spokenLanguage, (prompt, input, token) =>
        {
            var provider = pluginManager.LlmProviders.FirstOrDefault(candidate =>
                string.Equals(candidate.GetLlmSelectionId(), model.ProviderId, StringComparison.Ordinal));
            if (provider is null
                || !IsLocal(provider)
                || !provider.IsAvailable
                || !LocalModels(provider).Any(candidate =>
                    string.Equals(candidate.Id, model.ModelId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The local model is no longer available.");
            }

            return provider.ProcessAsync(prompt, input, model.ModelId, token);
        }, ct);
    }

    // Locality comes from the owning plugin's normalized metadata; an unknown owner fails closed.
    // A user-controlled server also counts when its endpoint is provably this computer.
    private bool IsLocal(ILlmProviderRole provider) =>
        pluginManager.GetPlugin(provider.PluginId)?.Metadata.NetworkAccess switch
        {
            PluginNetworkAccess.Local => true,
            PluginNetworkAccess.UserControlled =>
                provider is ILlmProviderEndpoint endpoint && LoopbackEndpoint.IsLoopback(endpoint.LlmEndpoint),
            _ => false,
        };

    // A local server can list models that it runs remotely; Ollama names those "…:cloud" or "…-cloud".
    private static IEnumerable<PluginModelInfo> LocalModels(ILlmProviderRole provider) =>
        provider is ILlmProviderEndpoint
            ? provider.SupportedModels.Where(model =>
                !model.Id.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)
                && !model.Id.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
            : provider.SupportedModels;
}
