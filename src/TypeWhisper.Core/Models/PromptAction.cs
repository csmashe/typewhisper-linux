// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace TypeWhisper.Core.Models;

/// <summary>
///     A configurable LLM action — a named system prompt plus optional
///     provider/model/plugin routing — that the user can run against transcribed
///     or selected text. Ships as presets (<see cref="IsPreset" />) and as
///     user-authored actions.
/// </summary>
public sealed record PromptAction
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SystemPrompt { get; init; }
    public string Icon { get; init; } = "\u2728";
    public bool IsPreset { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public string? ProviderOverride { get; init; }
    public string? ModelOverride { get; init; }
    public string? TargetActionPluginId { get; init; }
    public string? HotkeyKey { get; init; }
    public bool IsManualOnly { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}