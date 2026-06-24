namespace TypeWhisper.Core.Models;

/// <summary>The concrete formatting flags a <see cref="ProfileStylePreset" /> resolves to (cleanup level, smart/developer formatting, terminal-safe output).</summary>
public sealed record ProfileStyleSettings
{
    public required ProfileStylePreset Preset { get; init; }
    public CleanupLevel CleanupLevel { get; init; }
    public bool SmartFormattingEnabled { get; init; }
    public bool DeveloperFormattingEnabled { get; init; }
    public bool TerminalSafe { get; init; }
}
