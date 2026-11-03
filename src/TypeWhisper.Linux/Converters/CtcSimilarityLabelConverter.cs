using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Converters;

public sealed class CtcSimilarityLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DictionarySectionViewModel.DescribeCtcSimilarity(value as float?);

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => BindingOperations.DoNothing;
}
