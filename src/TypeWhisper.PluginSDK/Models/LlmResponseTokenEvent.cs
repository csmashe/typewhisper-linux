// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>Raised as an LLM response streams in, carrying the accumulated text.</summary>
// ReSharper disable once UnusedType.Global
public sealed record LlmResponseTokenEvent : PluginEvent
{
    /// <summary>Full accumulated response text so far.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string AccumulatedText { get; init; }

    /// <summary>The delta appended since the previous event (for bus subscribers).</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string DeltaText { get; init; } = "";

    /// <summary>True on the terminal flush (stream completed or faulted).</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public bool IsFinal { get; init; }

    /// <summary>True when the terminal flush is due to a mid-stream fault.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public bool Faulted { get; init; }

    /// <summary>Pipeline step that produced this text. Bare string to avoid SDK dependency on TypeWhisper.Core.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? StepName { get; init; }
}
