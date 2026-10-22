using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>A ready model of a local LLM role that may generate alias suggestions.</summary>
public sealed record LocalAliasModel(string ProviderId, string ModelId, string DisplayName);

/// <summary>
///     Generates alias suggestions with local LLMs only, never a cloud provider. The role is
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
                models.AddRange(provider.SupportedModels.Select(model => new LocalAliasModel(
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
                || !provider.SupportedModels.Any(candidate =>
                    string.Equals(candidate.Id, model.ModelId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The local model is no longer available.");
            }

            return provider.ProcessAsync(prompt, input, model.ModelId, token);
        }, ct);
    }

    // Locality comes from the owning plugin's normalized metadata; an unknown owner fails closed.
    private bool IsLocal(ILlmProviderRole provider) =>
        pluginManager.GetPlugin(provider.PluginId)?.Metadata.NetworkAccess == PluginNetworkAccess.Local;
}
