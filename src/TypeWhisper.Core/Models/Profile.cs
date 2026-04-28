namespace TypeWhisper.Core.Models;

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
    public ProfileStylePreset StylePreset { get; init; } = ProfileStylePreset.Raw;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

public enum ProfileStylePreset
{
    Raw,
    Clean,
    Concise,
    FormalEmail,
    CasualMessage,
    Developer,
    TerminalSafe,
    MeetingNotes
}

public sealed record ProfileStyleSettings
{
    public required ProfileStylePreset Preset { get; init; }
    public CleanupLevel CleanupLevel { get; init; }
    public bool SmartFormattingEnabled { get; init; }
    public bool DeveloperFormattingEnabled { get; init; }
    public bool TerminalSafe { get; init; }
}
