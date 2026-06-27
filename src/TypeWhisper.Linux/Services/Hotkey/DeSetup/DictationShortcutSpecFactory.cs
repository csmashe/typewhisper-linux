using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Builds the <see cref="DeShortcutSpec" /> for TypeWhisper's dictation toggle. Shared by the
///     Shortcuts panel and onboarding checklist so both register the same id/trigger/command —
///     a divergence would let one surface install a shortcut the other can't detect.
/// </summary>
public static class DictationShortcutSpecFactory
{
    public const string DictationShortcutId = "typewhisper.dictation.toggle";
    private const string DictationDisplayName = "TypeWhisper: Toggle Dictation";
    private const string DefaultTrigger = "Ctrl+Shift+Space";

    /// <summary>
    ///     Builds the spec for <paramref name="writer" />. PTT desktops (Hyprland/Sway) get
    ///     press/release/cancel triplet; toggle-only desktops (GNOME/KDE) get a single command.
    /// </summary>
    public static DeShortcutSpec Build(ISettingsService settings, IDeShortcutWriter writer)
    {
        var trigger = string.IsNullOrWhiteSpace(settings.Current.ToggleHotkey)
            ? DefaultTrigger
            : settings.Current.ToggleHotkey;
        var gui = ResolveGuiCommand();

        if (writer.SupportsPushToTalk)
        {
            return new DeShortcutSpec(
                DictationShortcutId,
                DictationDisplayName,
                trigger,
                $"{gui} record start",
                $"{gui} record stop",
                SwapKeyForCancel(trigger),
                $"{gui} record cancel"
            );
        }

        return new DeShortcutSpec(
            DictationShortcutId,
            DictationDisplayName,
            trigger,
            gui,
            null,
            null,
            null
        );
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

    private static string SwapKeyForCancel(string trigger)
    {
        var parts = trigger.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (parts.Length == 0)
        {
            return "Ctrl+Shift+Escape";
        }

        parts[^1] = "Escape";
        return string.Join('+', parts);
    }
}