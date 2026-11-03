namespace TypeWhisper.Core.Models;

/// <summary>A named output-style preset a profile applies to dictated text; <see cref="ProfileStyleSettings" /> holds what each preset expands to.</summary>
public enum ProfileStylePreset
{
    Raw,
    Clean,
    Concise,
    FormalEmail,
    CasualMessage,
    Developer,
    TerminalSafe,
    MeetingNotes,
}
