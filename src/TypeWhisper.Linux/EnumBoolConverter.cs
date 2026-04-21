using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace TypeWhisper.Linux;

/// <summary>
/// Two-way bridge between an enum value and a RadioButton's IsChecked:
///   Convert:      value == parameter
///   ConvertBack:  value==true → parameter;  value==false → DoNothing
///
/// Keeps the source enum in sync when a radio is selected while preventing
/// the deselected radio from accidentally clearing the source back to the
/// target's default.
/// </summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public static readonly EnumBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && value.Equals(parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
            return parameter;
        return BindingOperations.DoNothing;
    }
}
