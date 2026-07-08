// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace TypeWhisper.Core.Models;

/// <summary>
///     Fine-grained provenance of a single LLM call made while processing one
///     dictation: the exact system + user prompt that were sent, the response
///     that came back, the resolved provider/model, whether the call stayed
///     on-device, and (for prompt actions) any memory context that was injected.
///     Captured at the single chokepoint where the prompt is assembled and
///     persisted onto the owning <see cref="TranscriptionRecord" /> so each run
///     is auditable.
/// </summary>
public sealed record LlmCallProvenance
{
    /// <summary>Which pipeline stage issued the call: "Cleanup", "PromptAction", "Translation", or "Memory".</summary>
    public required string Stage { get; init; }

    /// <summary>The final system prompt sent to the provider (memory context already appended).</summary>
    public required string SystemPromptSent { get; init; }

    /// <summary>Exactly what was sent as the user message (post FormatPromptActionInput).</summary>
    public required string UserPromptSent { get; init; }

    /// <summary>
    ///     The reply the provider returned for this call. Set after the call
    ///     completes (settable, not init), so it is null until then and for
    ///     records produced before this field existed. On a streamed call it holds
    ///     the accumulated text (partial if the stream faulted mid-way).
    /// </summary>
    public string? ResponseReceived { get; set; }

    /// <summary>Human-readable provider name, e.g. "OpenAI".</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>Provider selection id used to resolve the plugin.</summary>
    public string ProviderId { get; init; } = "";

    /// <summary>The model id the call ran against.</summary>
    public string ModelId { get; init; } = "";

    /// <summary>True only when the provider is a verified on-device plugin (default: network).</summary>
    public bool RanLocally { get; init; }

    /// <summary>Memory context injected into the system prompt (prompt-action stage only); null otherwise.</summary>
    public string? InjectedMemoryContext { get; init; }
}
