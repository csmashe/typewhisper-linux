using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TypeWhisper.Linux;

/// <summary>
/// Maps a bool to a red (true) or gray (false) Color, used by the
/// recording indicator dot.
/// </summary>
public sealed class BoolColorConverter : IValueConverter
{
    public static readonly BoolColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Color.FromRgb(230, 60, 60) : Color.FromRgb(130, 130, 130);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
