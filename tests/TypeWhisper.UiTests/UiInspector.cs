using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace TypeWhisper.UiTests;

internal sealed record UiFinding(string Kind, string Control, string Detail, string Location);

/// <summary>Checks a rendered visual tree for problems a person would see or a screen reader would hit.</summary>
internal static partial class UiInspector
{
    internal const string RawKey = "raw-localization-key";
    internal const string ClippedText = "clipped-text";
    internal const string UnnamedControl = "unnamed-control";
    internal const string WrongLanguage = "wrong-language-text";

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$")]
    private static partial Regex KeyShape();

    /// <param name="foreignText">Catalog text from other UI languages that is absent from the rendered
    ///     language and the English fallback; seeing it on screen means stale or leaked text.</param>
    internal static List<UiFinding> Inspect(Visual root, IReadOnlySet<string> knownKeys, IReadOnlySet<string>? foreignText = null)
    {
        var findings = new List<UiFinding>();
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is not Control control || !control.IsEffectivelyVisible || control.Bounds.Width <= 0)
                continue;

            if (control is TextBlock text && text.Text is { Length: > 0 } value)
            {
                var trimmed = value.Trim();
                if (knownKeys.Contains(trimmed) || KeyShape().IsMatch(trimmed))
                    findings.Add(new UiFinding(RawKey, nameof(TextBlock), trimmed, Locate(control)));
                if (foreignText is not null && foreignText.Contains(trimmed))
                    findings.Add(new UiFinding(WrongLanguage, nameof(TextBlock), trimmed, Locate(control)));

                // The layout width is the widest line. With wrapping on, a line wider than the box
                // is a single word that cannot break, so wrapped text is checked too.
                // A wrapped line ends at a space that is not drawn, so it is measured without it.
                var needed = text.TextWrapping == TextWrapping.NoWrap
                    ? text.TextLayout.WidthIncludingTrailingWhitespace
                    : text.TextLayout.Width;
                if (text.TextTrimming == TextTrimming.None && needed > text.Bounds.Width + 1.5)
                {
                    findings.Add(new UiFinding(ClippedText, nameof(TextBlock),
                        $"\"{Shorten(trimmed)}\" needs {needed:0} px, has {text.Bounds.Width:0} px", Locate(control)));
                }
            }

            // Template parts (scrollbar buttons, a ComboBox's toggle) are named by their owner.
            if (control.TemplatedParent is null && IsInteractive(control) && string.IsNullOrWhiteSpace(AccessibleName(control)))
                findings.Add(new UiFinding(UnnamedControl, control.GetType().Name, Describe(control), Locate(control)));
        }
        return findings;
    }

    private static bool IsInteractive(Control control) =>
        control is Button or ToggleButton or ComboBox or TextBox or Slider or NumericUpDown or AutoCompleteBox;

    internal static string AccessibleName(Control control)
    {
        var explicitName = Avalonia.Automation.AutomationProperties.GetName(control);
        if (!string.IsNullOrWhiteSpace(explicitName))
            return explicitName;
        try
        {
            return ControlAutomationPeer.CreatePeerForElement(control).GetName() ?? "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static string Describe(Control control)
    {
        var hint = control switch
        {
            TextBox box => box.PlaceholderText is { Length: > 0 } placeholder ? $"placeholder \"{placeholder}\"" : null,
            ToolTip => null,
            _ => null,
        } ?? (ToolTip.GetTip(control) is { } tip ? $"tooltip \"{tip}\"" : null);
        var name = string.IsNullOrEmpty(control.Name) ? "" : $"x:Name={control.Name} ";
        return (name + (hint ?? "no text, tooltip, or label")).Trim();
    }

    /// <summary>The nearest labels above the control, so a finding can be found in the view.</summary>
    private static string Locate(Control control)
    {
        var parts = new List<string>();
        Visual? current = control.GetVisualParent();
        while (current is not null && parts.Count < 3)
        {
            if (current is Control parent)
            {
                var label = parent.GetVisualChildren().OfType<TextBlock>()
                    .Select(block => block.Text).FirstOrDefault(textValue => !string.IsNullOrWhiteSpace(textValue));
                if (!string.IsNullOrEmpty(parent.Name))
                    parts.Add(parent.Name!);
                else if (label is not null)
                    parts.Add($"near \"{Shorten(label)}\"");
                if (parent.GetType().Name.EndsWith("Section", StringComparison.Ordinal))
                {
                    parts.Add(parent.GetType().Name);
                    break;
                }
            }
            current = current.GetVisualParent();
        }
        return string.Join(" < ", parts);
    }

    private static string Shorten(string value) => value.Length <= 48 ? value : value[..45] + "...";
}
