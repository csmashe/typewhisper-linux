using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Builds the <see cref="DeShortcutSpec" /> for TypeWhisper's selected recording mode. Shared by the
///     Shortcuts panel and onboarding checklist so both register the same id/trigger/command —
///     a divergence would let one surface install a shortcut the other can't detect.
/// </summary>
public static class DictationShortcutSpecFactory
{
    public const string DictationShortcutId = "typewhisper.dictation.toggle";
    private const string DictationDisplayName = "TypeWhisper: Toggle Dictation";
    private const string DefaultTrigger = "Ctrl+Shift+Space";

    /// <summary>
    ///     Builds the spec for the selected recording mode and <paramref name="writer" />. Toggle uses
    ///     a press-only command on every desktop, PushToTalk requires press/release support, and Hybrid
    ///     is unsupported because native desktop bindings cannot reproduce its tap/hold threshold.
    /// </summary>
    public static DeShortcutSpec? Build(ISettingsService settings, IDeShortcutWriter writer)
    {
        var trigger = string.IsNullOrWhiteSpace(settings.Current.ToggleHotkey)
            ? DefaultTrigger
            : settings.Current.ToggleHotkey;
        var gui = ResolveGuiCommand();
        var cancelTrigger = SwapKeyForCancel(trigger);

        return settings.Current.Mode switch
        {
            RecordingMode.Toggle => new DeShortcutSpec(
                DictationShortcutId,
                DictationDisplayName,
                trigger,
                gui,
                null,
                null,
                null
            ),
            RecordingMode.PushToTalk when writer.SupportsPushToTalk => new DeShortcutSpec(
                DictationShortcutId,
                DictationDisplayName,
                trigger,
                $"{gui} record start",
                $"{gui} record stop",
                cancelTrigger,
                cancelTrigger is null ? null : $"{gui} record cancel"
            ),
            _ => null,
        };
    }

    /// <summary>
    ///     Returns the command for the auto-installed shortcut: the full apphost path when running as
    ///     the installed binary, otherwise the bare <c>typewhisper</c> name. ProcessPath is not
    ///     trusted when it points at the dotnet host (source/IDE runs).
    /// </summary>
    private static string ResolveGuiCommand()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)
            && string.Equals(Path.GetFileName(path), "typewhisper", StringComparison.Ordinal))
        {
            return path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
        }

        return "typewhisper";
    }

    /// <summary>
    ///     Derives the cancel accelerator by swapping the trigger's final key for Escape, or returns
    ///     null when that yields the recording trigger itself (a trigger already ending in Escape,
    ///     e.g. Ctrl+Shift+Escape). Binding start and cancel to one accelerator would fire both
    ///     commands, so the cancel bind is dropped instead — writers skip it for a null trigger.
    /// </summary>
    private static string? SwapKeyForCancel(string trigger)
    {
        var parts = trigger.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (parts.Length == 0)
        {
            return "Ctrl+Shift+Escape";
        }

        // Compare against the trigger rebuilt from the same parts so spacing and casing
        // differences ("ctrl + shift + escape") can't hide a collision.
        var normalizedTrigger = string.Join('+', parts);
        parts[^1] = "Escape";
        var cancel = string.Join('+', parts);
        return string.Equals(cancel, normalizedTrigger, StringComparison.OrdinalIgnoreCase)
            ? null
            : cancel;
    }
}
