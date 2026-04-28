using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public static class ProfileStylePresetService
{
    public static ProfileStyleSettings Resolve(ProfileStylePreset preset) =>
        preset switch
        {
            ProfileStylePreset.Raw => Settings(preset, CleanupLevel.None),
            ProfileStylePreset.Clean => Settings(preset, CleanupLevel.Light, smartFormatting: true),
            ProfileStylePreset.Concise => Settings(preset, CleanupLevel.High, smartFormatting: true),
            ProfileStylePreset.FormalEmail => Settings(preset, CleanupLevel.Medium, smartFormatting: true),
            ProfileStylePreset.CasualMessage => Settings(preset, CleanupLevel.Light, smartFormatting: true),
            ProfileStylePreset.Developer => Settings(preset, CleanupLevel.None, developerFormatting: true, terminalSafe: true),
            ProfileStylePreset.TerminalSafe => Settings(preset, CleanupLevel.None, developerFormatting: true, terminalSafe: true),
            ProfileStylePreset.MeetingNotes => Settings(preset, CleanupLevel.Medium, smartFormatting: true),
            _ => Settings(ProfileStylePreset.Raw, CleanupLevel.None)
        };

    private static ProfileStyleSettings Settings(
        ProfileStylePreset preset,
        CleanupLevel cleanupLevel,
        bool smartFormatting = false,
        bool developerFormatting = false,
        bool terminalSafe = false) =>
        new()
        {
            Preset = preset,
            CleanupLevel = cleanupLevel,
            SmartFormattingEnabled = smartFormatting,
            DeveloperFormattingEnabled = developerFormatting,
            TerminalSafe = terminalSafe
        };
}
