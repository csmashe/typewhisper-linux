using Avalonia.Data.Converters;
using System.Globalization;

namespace TypeWhisper.Linux;

/// <summary>
///     Maps a bool to one of two strings via a converter parameter in the form:
///     <c>True=yes|False=no</c>. If the parameter is absent or a key is missing,
///     the original value is returned unchanged.
/// </summary>
public sealed class BoolTextConverter : IValueConverter
{
    public static readonly BoolTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool boolValue || parameter is not string mapping)
        {
            return value;
        }

        var parts = mapping.Split(
            '|',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        string? trueValue = null;
        string? falseValue = null;

        foreach (var part in parts)
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (string.Equals(pair[0], "True", StringComparison.OrdinalIgnoreCase))
            {
                trueValue = pair[1];
            }
            else if (string.Equals(pair[0], "False", StringComparison.OrdinalIgnoreCase))
            {
                falseValue = pair[1];
            }
        }

        return boolValue ? trueValue ?? value : falseValue ?? value;
    }

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        return value;
    }
}