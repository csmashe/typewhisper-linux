using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Builds the <see cref="DeShortcutSpec" /> for TypeWhisper's dictation
///     toggle from the user's saved hotkey. Shared by the Shortcuts settings
///     panel and the onboarding setup checklist so both register exactly the
///     same shortcut id, trigger, and command — a divergence would let one
///     surface "install" a shortcut the other can't detect as installed.
/// </summary>
public static class DictationShortcutSpecFactory
{
    public const string DictationShortcutId = "typewhisper.dictation.toggle";
    public const string DictationDisplayName = "TypeWhisper: Toggle Dictation";
    public const string DefaultTrigger = "Ctrl+Shift+Space";

    /// <summary>
    ///     Construct the spec for <paramref name="writer" />. PTT-capable
    ///     desktops (Hyprland/Sway) get the press/release/cancel triplet that
    ///     drives the CLI directly; toggle-only desktops (GNOME/KDE) get a
    ///     single command.
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
    ///     The command the auto-installed shortcut should invoke. Resolves to
    ///     the GUI apphost path when launched as the installed <c>typewhisper</c>
    ///     binary, otherwise the bare <c>typewhisper</c> name. See the long
    ///     note in the Shortcuts panel for why we don't trust ProcessPath when
    ///     it points at the dotnet host (source / IDE runs).
    /// </summary>
    public static string ResolveGuiCommand()
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
