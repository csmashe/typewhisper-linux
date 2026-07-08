using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux;

/// <summary>
///     Maps a <see cref="DiffKind" /> to the foreground brush the history Inspect
///     panel uses for an inline word-diff run: green for added, red for removed,
///     a neutral light tone for unchanged.
/// </summary>
public sealed class DiffKindBrushConverter : IValueConverter
{
    public static readonly DiffKindBrushConverter Instance = new();

    private static readonly IBrush s_added = Brush.Parse("#5FD79A");
    private static readonly IBrush s_removed = Brush.Parse("#F06A6A");
    private static readonly IBrush s_unchanged = Brush.Parse("#C6D2E0");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DiffKind.Added => s_added,
            DiffKind.Removed => s_removed,
            _ => s_unchanged
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
///     Maps a <see cref="DiffKind" /> to the text decorations for an inline
///     word-diff run: removed runs are struck through, everything else is plain.
/// </summary>
public sealed class DiffKindDecorationsConverter : IValueConverter
{
    public static readonly DiffKindDecorationsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DiffKind.Removed ? TextDecorations.Strikethrough : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
