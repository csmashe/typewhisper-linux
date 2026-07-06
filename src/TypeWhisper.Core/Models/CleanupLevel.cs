namespace TypeWhisper.Core.Models;

/// <summary>How aggressively transcribed text is post-processed, from no cleanup up to heavy rewriting.</summary>
public enum CleanupLevel
{
    None,
    Light,
    Medium,
    High
}
