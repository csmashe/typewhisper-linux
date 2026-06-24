using System.Diagnostics;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Extracts lasting personal facts from dictated speech via an LLM and
///     stores them in the configured memory plugin. Facts are queried at
///     prompt-processing time to inject relevant context into LLM requests.
/// </summary>
public sealed class MemoryService
{
    private const int MinTextLength = 30;

    private const string ExtractionPrompt = """
                                            Extract any lasting personal facts from the following transcribed speech.
                                            Facts include: names, job titles, preferences, locations, relationships,
                                            projects, tools used, responsibilities, or recurring topics.

                                            Return ONLY the facts as a bullet list (one per line, starting with "- ").
                                            If there are no lasting facts, return exactly "NONE".
                                            Do not include temporary information like meeting times or deadlines.
                                            """;

    // Per-session rate-limit: extraction is an LLM call and should not fire
    // on every short dictation. 30 s is long enough to batch conversational
    // speech but short enough that a single long session still gets facts
    // extracted in a timely manner.
    private static readonly TimeSpan s_cooldown = TimeSpan.FromSeconds(30);

    private readonly PluginManager _pluginManager;
    private DateTime _lastExtraction = DateTime.MinValue;

    public MemoryService(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
    }

    public async Task ExtractAndStoreAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinTextLength)
        {
            return;
        }

        if (DateTime.UtcNow - _lastExtraction < s_cooldown)
        {
            return;
        }

        var plugins = _pluginManager.GetPlugins<IMemoryStoragePlugin>();
        var memoryPlugin = plugins.Count > 0 ? plugins[0] : null;
        if (memoryPlugin is null)
        {
            return;
        }

        var llm = _pluginManager.LlmProviders.FirstOrDefault(provider => provider.IsAvailable);
        if (llm is null)
        {
            return;
        }

        var model = (llm.SupportedModels.Count > 0 ? llm.SupportedModels[0] : null)?.Id;
        if (model is null)
        {
            return;
        }

        try
        {
            _lastExtraction = DateTime.UtcNow;

            var result = await llm.ProcessAsync(ExtractionPrompt, text, model, ct);
            if (
                string.IsNullOrWhiteSpace(result)
                || result.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase)
            )
            {
                return;
            }

            var facts = result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
                .Select(line => line[2..].Trim())
                .Where(fact => fact.Length > 5);

            foreach (var fact in facts)
            {
                await memoryPlugin.StoreAsync(fact, ct);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryService] extraction failed: {ex.Message}");
        }
    }

    public async Task<string?> GetContextAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var plugins = _pluginManager.GetPlugins<IMemoryStoragePlugin>();
        var memoryPlugin = plugins.Count > 0 ? plugins[0] : null;
        if (memoryPlugin is null)
        {
            return null;
        }

        try
        {
            var memories = await memoryPlugin.SearchAsync(query, 10, ct);
            return memories.Count == 0
                ? null
                : string.Join("\n", memories.Select(memory => $"- {memory}"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MemoryService] context lookup failed: {ex.Message}");
            return null;
        }
    }
}