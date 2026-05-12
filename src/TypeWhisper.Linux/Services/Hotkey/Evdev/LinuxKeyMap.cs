using SharpHook.Native;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
/// Maps Linux kernel <c>KEY_*</c> codes (from <c>linux/input-event-codes.h</c>)
/// to the SharpHook <see cref="KeyCode"/> enum and to <see cref="ModifierMask"/>
/// bits so the evdev backend can feed events into the shared
/// <see cref="ShortcutDispatcher"/> without diverging from SharpHook semantics.
///
/// Coverage tracks the chord keys the existing hotkey parser supports
/// (letters, digits, F1–F24, named keys, four modifier groups). Unmapped
/// codes return <see langword="null"/> and are dropped.
/// </summary>
internal static class LinuxKeyMap
{
    // Modifier KEY_* codes (input-event-codes.h)
    public const int KEY_LEFTCTRL = 29;
    public const int KEY_LEFTSHIFT = 42;
    public const int KEY_RIGHTSHIFT = 54;
    public const int KEY_LEFTALT = 56;
    public const int KEY_RIGHTCTRL = 97;
    public const int KEY_RIGHTALT = 100;
    public const int KEY_LEFTMETA = 125;
    public const int KEY_RIGHTMETA = 126;

    public static ModifierMask ToModifier(int linuxCode) => linuxCode switch
    {
        KEY_LEFTCTRL => ModifierMask.LeftCtrl,
        KEY_RIGHTCTRL => ModifierMask.RightCtrl,
        KEY_LEFTSHIFT => ModifierMask.LeftShift,
        KEY_RIGHTSHIFT => ModifierMask.RightShift,
        KEY_LEFTALT => ModifierMask.LeftAlt,
        KEY_RIGHTALT => ModifierMask.RightAlt,
        KEY_LEFTMETA => ModifierMask.LeftMeta,
        KEY_RIGHTMETA => ModifierMask.RightMeta,
        _ => ModifierMask.None,
    };

    public static bool IsModifier(int linuxCode) => ToModifier(linuxCode) != ModifierMask.None;

    public static KeyCode? ToSharpHook(int linuxCode) => linuxCode switch
    {
        // Row 1 — digits
        2 => KeyCode.Vc1,
        3 => KeyCode.Vc2,
        4 => KeyCode.Vc3,
        5 => KeyCode.Vc4,
        6 => KeyCode.Vc5,
        7 => KeyCode.Vc6,
        8 => KeyCode.Vc7,
        9 => KeyCode.Vc8,
        10 => KeyCode.Vc9,
        11 => KeyCode.Vc0,

        // Row 2 — letters QWERTY
        16 => KeyCode.VcQ,
        17 => KeyCode.VcW,
        18 => KeyCode.VcE,
        19 => KeyCode.VcR,
        20 => KeyCode.VcT,
        21 => KeyCode.VcY,
        22 => KeyCode.VcU,
        23 => KeyCode.VcI,
        24 => KeyCode.VcO,
        25 => KeyCode.VcP,
        30 => KeyCode.VcA,
        31 => KeyCode.VcS,
        32 => KeyCode.VcD,
        33 => KeyCode.VcF,
        34 => KeyCode.VcG,
        35 => KeyCode.VcH,
        36 => KeyCode.VcJ,
        37 => KeyCode.VcK,
        38 => KeyCode.VcL,
        44 => KeyCode.VcZ,
        45 => KeyCode.VcX,
        46 => KeyCode.VcC,
        47 => KeyCode.VcV,
        48 => KeyCode.VcB,
        49 => KeyCode.VcN,
        50 => KeyCode.VcM,

        // Named keys
        1 => KeyCode.VcEscape,
        14 => KeyCode.VcBackspace,
        15 => KeyCode.VcTab,
        28 => KeyCode.VcEnter,
        57 => KeyCode.VcSpace,
        103 => KeyCode.VcUp,
        105 => KeyCode.VcLeft,
        106 => KeyCode.VcRight,
        108 => KeyCode.VcDown,
        102 => KeyCode.VcHome,
        107 => KeyCode.VcEnd,
        104 => KeyCode.VcPageUp,
        109 => KeyCode.VcPageDown,
        110 => KeyCode.VcInsert,
        111 => KeyCode.VcDelete,

        // F1–F24
        59 => KeyCode.VcF1,
        60 => KeyCode.VcF2,
        61 => KeyCode.VcF3,
        62 => KeyCode.VcF4,
        63 => KeyCode.VcF5,
        64 => KeyCode.VcF6,
        65 => KeyCode.VcF7,
        66 => KeyCode.VcF8,
        67 => KeyCode.VcF9,
        68 => KeyCode.VcF10,
        87 => KeyCode.VcF11,
        88 => KeyCode.VcF12,
        183 => KeyCode.VcF13,
        184 => KeyCode.VcF14,
        185 => KeyCode.VcF15,
        186 => KeyCode.VcF16,
        187 => KeyCode.VcF17,
        188 => KeyCode.VcF18,
        189 => KeyCode.VcF19,
        190 => KeyCode.VcF20,
        191 => KeyCode.VcF21,
        192 => KeyCode.VcF22,
        193 => KeyCode.VcF23,
        194 => KeyCode.VcF24,

        // Modifiers — also mapped so a chord *whose key is the modifier*
        // (e.g. user binds Right Ctrl alone) still resolves.
        KEY_LEFTCTRL => KeyCode.VcLeftControl,
        KEY_RIGHTCTRL => KeyCode.VcRightControl,
        KEY_LEFTSHIFT => KeyCode.VcLeftShift,
        KEY_RIGHTSHIFT => KeyCode.VcRightShift,
        KEY_LEFTALT => KeyCode.VcLeftAlt,
        KEY_RIGHTALT => KeyCode.VcRightAlt,
        KEY_LEFTMETA => KeyCode.VcLeftMeta,
        KEY_RIGHTMETA => KeyCode.VcRightMeta,

        _ => null,
    };
}
