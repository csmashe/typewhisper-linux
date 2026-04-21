using System.Globalization;
using Avalonia.Data.Converters;

namespace TypeWhisper.Linux;

/// <summary>
/// Converts an enum value to bool by comparing to a parameter, for use with
/// RadioButton IsChecked bindings in XAML. ConvertBack is intentionally
/// one-way (commands drive the underlying property).
/// </summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public static readonly EnumBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && value.Equals(parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
