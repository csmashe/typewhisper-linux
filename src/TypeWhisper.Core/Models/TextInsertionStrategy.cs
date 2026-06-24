namespace TypeWhisper.Core.Models;

/// <summary>How transcribed text is delivered to the focused app: auto-pick, clipboard paste, simulated typing, or copy-only (no insertion).</summary>
public enum TextInsertionStrategy
{
    Auto,
    ClipboardPaste,
    DirectTyping,
    CopyOnly
}
