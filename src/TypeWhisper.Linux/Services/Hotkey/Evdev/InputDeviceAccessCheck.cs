namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Answers the ground-truth question the evdev backend actually cares about:
///     "can this process open at least one keyboard event node right now?"
///     <para>
///         This is the correct gate for the keyboard-access setup once the
///         <c>uaccess</c> udev rule is in play: the rule grants access via a
///         session ACL, so the user is <em>not</em> in the <c>input</c> group yet
///         access works. Checking group membership
///         (<see cref="InputGroupCheck.CurrentUserInInputGroup" />) would falsely
///         report "no access" and nag. Checking openability reflects reality on
///         both logind (uaccess) and non-logind (group) systems.
///     </para>
///     <para>
///         Probing is cheap: <see cref="KeyboardDeviceDiscovery.EnumerateKeyboards" />
///         already opens each candidate node to read its capability bits, so a
///         non-empty result implies at least one keyboard was openable. We
///         re-probe explicitly here so the intent reads clearly at call sites.
///     </para>
/// </summary>
public static class InputDeviceAccessCheck
{
    /// <summary>
    ///     True when at least one keyboard <c>/dev/input/event*</c> node can be
    ///     opened for reading. False when none can (no access, or no keyboards).
    /// </summary>
    public static bool HasKeyboardAccess()
    {
        foreach (var node in KeyboardDeviceDiscovery.EnumerateKeyboards())
        {
            try
            {
                using var stream = new FileStream(
                    node,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                );
                return true;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return false;
    }
}
