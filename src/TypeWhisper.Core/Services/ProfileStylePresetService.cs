using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public static class ProfileStylePresetService
{
    public static ProfileStyleSettings Resolve(ProfileStylePreset preset)
    {
        return preset switch
        {
            ProfileStylePreset.Raw => Settings(preset, CleanupLevel.None),
            ProfileStylePreset.Clean => Settings(preset, CleanupLevel.Light, true),
            ProfileStylePreset.Concise => Settings(
                preset,
                CleanupLevel.High,
                true
            ),
            ProfileStylePreset.FormalEmail => Settings(
                preset,
                CleanupLevel.Medium,
                true
            ),
            ProfileStylePreset.CasualMessage => Settings(
                preset,
                CleanupLevel.Light,
                true
            ),
            ProfileStylePreset.Developer => Settings(
                preset,
                CleanupLevel.None,
                developerFormatting: true
            ),
            ProfileStylePreset.TerminalSafe => Settings(
                preset,
                CleanupLevel.None,
                developerFormatting: true,
                terminalSafe: true
            ),
            ProfileStylePreset.MeetingNotes => Settings(
                preset,
                CleanupLevel.Medium,
                true
            ),
            _ => Settings(ProfileStylePreset.Raw, CleanupLevel.None)
        };
    }

    private static ProfileStyleSettings Settings(
        ProfileStylePreset preset,
        CleanupLevel cleanupLevel,
        bool smartFormatting = false,
        bool developerFormatting = false,
        bool terminalSafe = false
    )
    {
        return new ProfileStyleSettings
        {
            Preset = preset,
            CleanupLevel = cleanupLevel,
            SmartFormattingEnabled = smartFormatting,
            DeveloperFormattingEnabled = developerFormatting,
            TerminalSafe = terminalSafe
        };
    }
}