using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

public sealed class PromptProcessingService
{
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;

    public PromptProcessingService(PluginManager pluginManager, ISettingsService settings)
    {
        _pluginManager = pluginManager;
        _settings = settings;
    }

    public bool IsAnyProviderAvailable =>
        _pluginManager.LlmProviders.Any(provider => provider.IsAvailable);

    public async Task<string> ProcessAsync(PromptAction action, string inputText, CancellationToken ct)
    {
        var (provider, modelId) = ResolveProvider(action);
        if (provider is null)
            throw new InvalidOperationException("No enabled LLM provider is available.");

        return await provider.ProcessAsync(action.SystemPrompt, inputText, modelId, ct);
    }

    private (ILlmProviderPlugin? Provider, string ModelId) ResolveProvider(PromptAction action)
    {
        if (!string.IsNullOrWhiteSpace(action.ProviderOverride))
        {
            var overrideResult = ResolvePluginModelId(action.ProviderOverride);
            if (overrideResult.Provider is not null)
                return overrideResult;
        }

        if (!string.IsNullOrWhiteSpace(_settings.Current.DefaultLlmProvider))
        {
            var defaultResult = ResolvePluginModelId(_settings.Current.DefaultLlmProvider);
            if (defaultResult.Provider is not null)
                return defaultResult;
        }

        foreach (var provider in _pluginManager.LlmProviders)
        {
            if (!provider.IsAvailable)
                continue;

            var firstModel = provider.SupportedModels.FirstOrDefault();
            if (firstModel is not null)
                return (provider, firstModel.Id);
        }

        return (null, string.Empty);
    }

    private (ILlmProviderPlugin? Provider, string ModelId) ResolvePluginModelId(string pluginModelId)
    {
        var parts = pluginModelId.Split(':', 3);
        if (parts.Length < 3 || !string.Equals(parts[0], "plugin", StringComparison.Ordinal))
            return (null, string.Empty);

        var pluginId = parts[1];
        var modelId = parts[2];
        var plugin = _pluginManager.GetPlugin(pluginId)?.Instance;
        var provider = _pluginManager.LlmProviders.FirstOrDefault(candidate =>
            ReferenceEquals(candidate, plugin) && candidate.IsAvailable);

        return provider is null ? (null, string.Empty) : (provider, modelId);
    }
}
