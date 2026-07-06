// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace TypeWhisper.Core.Models;

/// <summary>
///     A context profile that overrides dictation behavior (language, task, model,
///     cleanup, style, linked prompt action, hotkey) when the active window's
///     process or URL matches its <see cref="ProcessNames" /> /
///     <see cref="UrlPatterns" />. <see cref="Priority" /> breaks ties between
///     profiles matching at the same specificity.
/// </summary>
public sealed record Profile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int Priority { get; init; }
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public IReadOnlyList<string> UrlPatterns { get; init; } = [];
    public string? InputLanguage { get; init; }
    public string? TranslationTarget { get; init; }
    public string? SelectedTask { get; init; }
    public bool? WhisperModeOverride { get; init; }
    public string? TranscriptionModelOverride { get; init; }
    public string? PromptActionId { get; init; }
    public string? HotkeyData { get; init; }
    public ProfileHotkeyBehavior HotkeyBehavior { get; init; } = ProfileHotkeyBehavior.StartDictation;
    public ProfileStylePreset StylePreset { get; init; } = ProfileStylePreset.Raw;
    public CleanupLevel? CleanupLevelOverride { get; init; }
    public bool? DeveloperFormattingOverride { get; init; }

    // Per-profile overrides for the two global reference-context toggles. Null =
    // inherit the global AppSettings value (same shape as WhisperModeOverride).
    public bool? ScreenContextOverride { get; init; }
    public bool? ClipboardContextOverride { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}