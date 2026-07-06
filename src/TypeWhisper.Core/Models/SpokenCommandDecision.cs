namespace TypeWhisper.Core.Models;

/// <summary>
///     How the LLM classifier decided a spoken command should be handled.
/// </summary>
public enum CommandKind
{
    /// <summary>Transform text that is already highlighted/selected in the target app.</summary>
    Edit,

    /// <summary>Generate new text from the instruction and insert it at the cursor.</summary>
    Create
}

/// <summary>
///     The classifier's verdict for a spoken command: whether it edits existing
///     text or creates new text, and which saved prompt action (if any) fits.
///     <see cref="ActionId" /> is null when no saved action applies.
/// </summary>
public sealed record SpokenCommandDecision(CommandKind Kind, string? ActionId);
