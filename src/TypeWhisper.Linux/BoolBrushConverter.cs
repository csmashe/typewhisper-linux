using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TypeWhisper.Linux;

/// <summary>
/// Maps a bool to one of two brushes via a parameter in the form:
/// True=#RRGGBB|False=#RRGGBB or True=Transparent|False=#RRGGBB
/// </summary>
public sealed class BoolBrushConverter : IValueConverter
{
    public static readonly BoolBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var boolValue = value is true;
        if (parameter is string raw)
        {
            var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var map = parts
                .Select(part => part.Split('=', 2))
                .Where(part => part.Length == 2)
                .ToDictionary(part => part[0], part => part[1], StringComparer.OrdinalIgnoreCase);

            if (map.TryGetValue(boolValue ? "True" : "False", out var brushValue))
                return Brush.Parse(brushValue);
        }

        return boolValue ? Brushes.White : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
