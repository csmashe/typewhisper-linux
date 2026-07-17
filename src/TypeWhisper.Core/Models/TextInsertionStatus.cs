namespace TypeWhisper.Core.Models;

/// <summary>Outcome of delivering transcribed text to the focused application (pasted, typed, copied, handled by an action, or a specific failure).</summary>
public enum TextInsertionStatus
{
    Unknown,
    Pasted,
    Typed,
    CopiedToClipboard,
    NoText,
    ActionHandled,
    ActionFailed,
    MissingClipboardTool,
    MissingPasteTool,
    Failed,

    // Appended after Failed to preserve the persisted numeric ordinals of the
    // members above: history.json serializes this enum by value (no string
    // converter), so inserting mid-enum would reinterpret existing records.
    ActionUnavailable
}
