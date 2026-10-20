namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>Translates TypeWhisper's existing key names into desktop accelerator spellings.</summary>
internal static class DesktopShortcutKey
{
    public static bool IsSideSpecificModifier(string trigger) => trigger.Trim().ToLowerInvariant() is
        "left ctrl" or "right ctrl" or "left shift" or "right shift" or
        "left alt" or "right alt" or "left meta" or "right meta";

    public static string ToKeysym(string key) => key.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => "Return",
        "escape" or "esc" => "Escape",
        "backspace" => "BackSpace",
        "delete" or "del" => "Delete",
        "tab" => "Tab",
        "home" => "Home",
        "end" => "End",
        "pageup" => "Page_Up",
        "pagedown" => "Page_Down",
        "left" => "Left",
        "right" => "Right",
        "up" => "Up",
        "down" => "Down",
        "left ctrl" => "Control_L",
        "right ctrl" => "Control_R",
        "left shift" => "Shift_L",
        "right shift" => "Shift_R",
        "left alt" => "Alt_L",
        "right alt" => "Alt_R",
        "left meta" => "Super_L",
        "right meta" => "Super_R",
        var name when name.Length >= 2 && name[0] == 'f' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24 => $"F{f}",
        var name => name,
    };

    public static string ToQtName(string key) => key.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => "Return",
        "escape" or "esc" => "Esc",
        "pageup" => "PgUp",
        "pagedown" => "PgDown",
        "delete" or "del" => "Del",
        _ => key,
    };
}
