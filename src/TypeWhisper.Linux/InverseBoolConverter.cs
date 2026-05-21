using Avalonia.Data.Converters;
using System.Globalization;

namespace TypeWhisper.Linux;

/// <summary>Negates a bool binding. Non-bool values pass through unchanged.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        return value is bool boolValue ? !boolValue : value;
    }

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        return value is bool boolValue ? !boolValue : value;
    }
}