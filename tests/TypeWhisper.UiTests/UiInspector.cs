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

    /// <param name="root">The rendered visual tree to inspect.</param>
    /// <param name="knownKeys">Localization keys; one shown verbatim is an untranslated leak.</param>
    /// <param name="foreignText">Catalog text from other UI languages that is absent from the rendered
    ///     language and the English fallback; seeing it on screen means stale or leaked text.</param>
    internal static List<UiFinding> Inspect(Visual root, IReadOnlySet<string> knownKeys, IReadOnlySet<string>? foreignText = null)
    {
        var findings = new List<UiFinding>();
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is not Control { IsEffectivelyVisible: true, Bounds.Width: > 0 } control)
                continue;

            if (control is TextBlock { Text: { Length: > 0 } value } text)
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
                var visibleWidth = Math.Min(text.Bounds.Width, ComputeVisibleWidth(text, root));
                if (text.TextTrimming == TextTrimming.None && needed > visibleWidth + 1.5)
                {
                    findings.Add(new UiFinding(ClippedText, nameof(TextBlock),
                        $"\"{Shorten(trimmed)}\" needs {needed:0} px, has {visibleWidth:0} px", Locate(control)));
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

    private static string AccessibleName(Control control)
    {
        var explicitName = Avalonia.Automation.AutomationProperties.GetName(control);
        if (!string.IsNullOrWhiteSpace(explicitName))
            return explicitName;
        try
        {
            return ControlAutomationPeer.CreatePeerForElement(control).GetName();
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
            _ => null,
        } ?? (ToolTip.GetTip(control) is { } tip ? $"tooltip \"{tip}\"" : null);
        var name = string.IsNullOrEmpty(control.Name) ? "" : $"x:Name={control.Name} ";
        return (name + (hint ?? "no text, tooltip, or label")).Trim();
    }

    /// <summary>The nearest labels above the control, so a finding can be found in the view.</summary>
    private static string Locate(Control control)
    {
        var parts = new List<string>();
        var current = control.GetVisualParent();
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


    private static double ComputeVisibleWidth(Control control, Visual root)
    {
        var transform = control.TransformToVisual(root);
        if (transform is null)
            return control.Bounds.Width;

        var rootRect = new Rect(0, 0, control.Bounds.Width, control.Bounds.Height).TransformToAABB(transform.Value);
        var clipLeft = 0.0;
        var clipRight = root.Bounds.Width;

        var current = control.GetVisualParent();
        while (current is not null && current != root)
        {
            if (current is Control ancestor && (ancestor.ClipToBounds || ancestor is ScrollViewer { HorizontalScrollBarVisibility: ScrollBarVisibility.Disabled }))
            {
                var ancestorTransform = ancestor.TransformToVisual(root);
                if (ancestorTransform is not null)
                {
                    var ancestorBounds = new Rect(0, 0, ancestor.Bounds.Width, ancestor.Bounds.Height)
                        .TransformToAABB(ancestorTransform.Value);
                    clipLeft = Math.Max(clipLeft, ancestorBounds.Left);
                    clipRight = Math.Min(clipRight, ancestorBounds.Right);
                }
            }
            current = current.GetVisualParent();
        }

        var visibleLeft = Math.Max(rootRect.Left, clipLeft);
        var visibleRight = Math.Min(rootRect.Right, clipRight);
        return Math.Max(0.0, visibleRight - visibleLeft);
    }

    private static string Shorten(string value) => value.Length <= 48 ? value : value[..45] + "...";

}
