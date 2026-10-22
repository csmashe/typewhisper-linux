using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Converters;

public sealed class AliasSuggestionsAvailableConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DictionaryEntry entry && DictionarySectionViewModel.CanSuggestAliasesFor(entry);

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => BindingOperations.DoNothing;
}
