using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace TypeWhisper.Linux;

public sealed class BoolColorConverter : IValueConverter
{
    public static readonly BoolColorConverter Instance = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        return value is true ? Color.FromRgb(230, 60, 60) : Color.FromRgb(130, 130, 130);
    }

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        throw new NotSupportedException();
    }
}