using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>
///     Decides what a Profile's global hotkey does when pressed:
///     <see cref="StartDictation" /> starts dictation forced to this profile
///     (overriding window/URL context matching), while
///     <see cref="ProcessSelectedText" /> runs the profile's linked
///     <c>PromptAction</c> against the current selection without dictating.
/// </summary>
[JsonConverter(
    typeof(JsonStringEnumConverter<ProfileHotkeyBehavior>))]
public enum ProfileHotkeyBehavior
{
    StartDictation,
    ProcessSelectedText,
}
