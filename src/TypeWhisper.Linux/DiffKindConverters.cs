using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux;

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

