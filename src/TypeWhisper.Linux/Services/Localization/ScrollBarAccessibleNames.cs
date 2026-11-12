using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace TypeWhisper.Linux.Services.Localization;

/// <summary>
///     The Fluent ScrollBar template names its buttons and thumb in English ("Line up", "Position").
///     A local binding outranks the template's value, so screen readers hear them in the UI language.
/// </summary>
public static class ScrollBarAccessibleNames
{
    private static readonly (string Part, string Key)[] s_buttons =
    [
        ("PART_LineUpButton", "Common.ScrollLineUp"),
        ("PART_PageUpButton", "Common.ScrollPageUp"),
        ("PART_PageDownButton", "Common.ScrollPageDown"),
        ("PART_LineDownButton", "Common.ScrollLineDown"),
    ];

    public static void Register() =>
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ScrollBar>((_, e) => Apply(e.NameScope));

    private static void Apply(INameScope nameScope)
    {
        foreach (var (part, key) in s_buttons)
        {
            if (nameScope.Find<Control>(part) is { } button)
            {
                Localize(button, key);
            }
        }

        if (nameScope.Find<Track>("PART_Track")?.Thumb is { } thumb)
        {
            Localize(thumb, "Common.ScrollPosition");
        }
    }

    private static void Localize(Control control, string key) =>
        control.Bind(AutomationProperties.NameProperty, (Binding)new StrExtension(key).ProvideValue(null!));
}
