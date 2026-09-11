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
    /// <summary>Only matters when both ProcessNames and UrlPatterns are non-empty.</summary>
    public ProfileContextMatchMode ContextMatchMode { get; init; } = ProfileContextMatchMode.All;
    public string? InputLanguage { get; init; }
    public IReadOnlyList<string> InputLanguageHints { get; init; } = [];
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
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     A blank InputLanguage inherits the global hints and "auto" clears them; otherwise the
    ///     profile language leads and any stored hints only extend it. The editor writes
    ///     InputLanguage alone, so hints never outrank what it shows.
    /// </summary>
    public IReadOnlyList<string> GetLanguageHints(IReadOnlyList<string> globalHints)
    {
        if (string.IsNullOrWhiteSpace(InputLanguage))
        {
            return AppSettings.NormalizeLanguageHints(globalHints);
        }

        // A hand-edited profiles.json may carry an explicit null in the hints.
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        return InputLanguage.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? []
            : AppSettings.NormalizeLanguageHints([InputLanguage, .. InputLanguageHints ?? []]);
    }
}
