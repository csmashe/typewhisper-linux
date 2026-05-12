namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
/// Per-desktop helper that installs (or removes) TypeWhisper's global
/// dictation shortcut without dragging the user through the DE's GUI.
///
/// Each implementation owns one desktop and is independent of the others
/// — the orchestration UI picks whichever one's <see cref="IsCurrentDesktop"/>
/// returns true. The interface is deliberately small: the UI button
/// pushes a <see cref="DeShortcutSpec"/>, the writer reports back what it
/// changed and whether the user needs to do anything else (reload, log
/// out, etc.).
/// </summary>
public interface IDeShortcutWriter
{
    /// <summary>Stable token used in IDs and logs ("gnome", "kde", "hyprland", "sway").</summary>
    string DesktopId { get; }

    /// <summary>Human-readable name shown next to the "Set up automatically" button.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Cheap synchronous check that only reads environment variables
    /// plus a `which`-style binary lookup. Must not invoke any helper
    /// command — startup latency budget for the Shortcuts panel is
    /// effectively zero. A `true` result is a necessary precondition
    /// for <see cref="WriteAsync"/> to succeed but does not guarantee it.
    /// </summary>
    bool IsCurrentDesktop();

    /// <summary>
    /// True if this DE can drive separate press/release commands — i.e.
    /// can run true hold-to-talk via compositor binds. GNOME and KDE
    /// can't and so ignore <see cref="DeShortcutSpec.OnReleaseCommand"/>.
    /// </summary>
    bool SupportsPushToTalk { get; }

    /// <summary>
    /// Preview of the lines / fields the writer would change, rendered
    /// for display in the Settings panel before the user commits. Pure
    /// — does not touch disk or invoke external commands.
    /// </summary>
    string PreviewLines(DeShortcutSpec spec);

    /// <summary>
    /// Install the shortcut. Idempotent: running twice with the same
    /// spec must produce the same final state, not duplicate entries.
    /// </summary>
    Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct);

    /// <summary>
    /// Remove the managed entry, leaving any non-managed user shortcuts
    /// intact. Safe to call when nothing was previously written.
    /// </summary>
    Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct);
}

/// <summary>
/// Inputs for <see cref="IDeShortcutWriter.WriteAsync"/>.
/// </summary>
/// <param name="ShortcutId">Stable identifier, e.g. "typewhisper.dictation.toggle".</param>
/// <param name="DisplayName">Name surfaced in the DE's own shortcut list.</param>
/// <param name="Trigger">Accelerator in TypeWhisper format, e.g. "Ctrl+Shift+Space".</param>
/// <param name="OnPressCommand">Command to run on key press (or on toggle).</param>
/// <param name="OnReleaseCommand">Command on key release for PTT; null for toggle-only DEs.</param>
/// <param name="OnCancelTrigger">Optional separate accelerator that maps to a cancel command (Hyprland/Sway only).</param>
/// <param name="OnCancelCommand">Command to run on the cancel trigger; null disables the cancel bind.</param>
public sealed record DeShortcutSpec(
    string ShortcutId,
    string DisplayName,
    string Trigger,
    string OnPressCommand,
    string? OnReleaseCommand,
    string? OnCancelTrigger,
    string? OnCancelCommand);

/// <summary>
/// Outcome of an <see cref="IDeShortcutWriter"/> operation.
/// </summary>
/// <param name="Success">True on success (or no-op removal); false if the user
/// needs to fix something (mismatched sentinels, missing binary, etc.).</param>
/// <param name="UserMessage">One-line message safe to show in the status panel.</param>
/// <param name="FilesChanged">Files (or schema paths) actually modified. Empty for no-ops.</param>
/// <param name="Warning">Optional non-fatal warning — e.g. "wrote config but hyprctl was unavailable, restart Hyprland to pick it up". Distinguished from <paramref name="UserMessage"/> so the UI can paint it differently.</param>
public sealed record DeShortcutWriteResult(
    bool Success,
    string? UserMessage,
    IReadOnlyList<string> FilesChanged,
    string? Warning = null);
