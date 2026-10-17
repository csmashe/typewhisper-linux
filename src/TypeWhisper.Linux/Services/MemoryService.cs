using System.Diagnostics;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Extracts lasting personal facts from dictated speech via an LLM and
///     stores them in the configured memory plugin. Facts are queried at
///     prompt-processing time to inject relevant context into LLM requests.
/// </summary>
public sealed partial class MemoryService
{
    private const int MinTextLength = 30;
    internal const int MaxRecalledFacts = 5;
    internal const int MaxFactLength = 1600;


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

    public async Task ExtractAndStoreAsync(
        string text,
        LlmCallCapture? capture = null,
        CancellationToken ct = default
    )
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
        // ReSharper disable once UseNullPropagation -- early-return null guard protecting later non-conditional dereferences of llm; there is no member access to fold into a null-conditional, so a rewrite would change control flow.
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

            // Record before the call so a mid-request fault is still captured.
            var provenance = RecordProvenance(capture, llm, model, text);

            var result = await llm.ProcessAsync(ExtractionPrompt, text, model, ct);
            provenance?.ResponseReceived = result;

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

    // Mirrors PromptProcessingService.RecordProvenance for the extraction call:
    // records that memory extraction also sent the dictation text to an LLM and
    // returns the entry so the caller can attach the response (null when capture
    // is disabled).
    private LlmCallProvenance? RecordProvenance(
        LlmCallCapture? capture,
        ILlmProviderRole provider,
        string modelId,
        string userPrompt
    )
    {
        if (capture is null)
        {
            return null;
        }

        var providerId = provider.GetLlmSelectionId();
        // Look the plugin up by its owning plugin ID: a profile-backed role's
        // selection ID is the profile's, which matches no manifest ID.
        var plugin = _pluginManager.GetPlugin(provider.PluginId);
        var ranLocally = plugin?.Metadata.RanLocally ?? false;

        var provenance = new LlmCallProvenance
        {
            Stage = "Memory",
            SystemPromptSent = ExtractionPrompt,
            UserPromptSent = userPrompt,
            ProviderName = provider.ProviderName,
            ProviderId = providerId,
            ModelId = modelId,
            RanLocally = ranLocally,
            InjectedMemoryContext = null,
        };
        capture.Add(provenance);
        return provenance;
    }

    /// <summary>
    ///     Global recall from the first memory plugin; a failing plugin counts as no context.
    /// </summary>
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
            return FormatContext(await memoryPlugin.SearchAsync(query, MaxRecalledFacts, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Debug.WriteLine($"[MemoryService] context lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Recall from the profile's chosen source only. When it is unavailable or fails, the
    ///     prompt action fails (the dictation stays in History) instead of running without it.
    /// </summary>
    public async Task<string?> GetContextFromSourceAsync(
        string query,
        string sourceId,
        CancellationToken ct = default
    )
    {
        var source = _pluginManager
            .GetPlugins<IMemoryStoragePlugin>()
            .FirstOrDefault(plugin => plugin.PluginId == sourceId);
        if (source is null)
        {
            throw new InvalidOperationException(Localization.Loc.Instance["Memory.SourceUnavailable"]);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        IReadOnlyList<string> matches;
        try
        {
            matches = await source.SearchAsync(query, MaxRecalledFacts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Debug.WriteLine($"[MemoryService] context lookup failed: {ex.Message}");
            throw new InvalidOperationException(Localization.Loc.Instance["Memory.SourceFailed"], ex);
        }

        // A source disabled or replaced while it searched must not supply the result.
        if (
            !_pluginManager.IsEnabled(sourceId)
            || !ReferenceEquals(_pluginManager.GetPlugin(sourceId)?.Instance, source)
        )
        {
            throw new InvalidOperationException(Localization.Loc.Instance["Memory.SourceUnavailable"]);
        }

        return FormatContext(matches);
    }

    // At most five facts of at most 1,600 characters, one per line, so a long or multiline
    // entry cannot crowd out the prompt or pose as a separate fact.
    internal static string? FormatContext(IReadOnlyList<string> memories)
    {
        var lines = memories
            .Select(memory => Whitespace().Replace(memory, " ").Trim())
            .Where(memory => memory.Length > 0)
            .Take(MaxRecalledFacts)
            .Select(memory => memory.Length > MaxFactLength ? memory[..MaxFactLength] : memory)
            .Select(memory => $"- {memory}")
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
