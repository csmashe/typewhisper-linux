using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace TypeWhisper.Linux.Services.Localization;

/// <summary>
///     XAML markup extension: <c>{loc:Str Some.Key}</c>.
///     Produces a one-way binding that re-evaluates whenever the UI language
///     changes, so localized text switches live without an app restart.
/// </summary>
/// <remarks>
///     We bind to <see cref="Loc.CurrentLanguage" /> (a normal property that
///     raises PropertyChanged) and resolve the key in a converter, rather than
///     binding to the <c>[key]</c> indexer directly. This sidesteps Avalonia
///     binding-path parsing of dotted keys *and* the compiled-bindings
///     requirement for an x:DataType, since the Source is set explicitly.
/// </remarks>
public sealed class StrExtension : MarkupExtension
{
    public StrExtension() { }

    public StrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding(nameof(Loc.CurrentLanguage))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = LocKeyConverter.Instance,
            ConverterParameter = Key
        };
    }
}

/// <summary>
///     Resolves the localized string for the key passed as the converter
///     parameter. The bound value (the current language) is ignored — it only
///     exists to trigger re-evaluation on language change.
/// </summary>
public sealed class LocKeyConverter : IValueConverter
{
    public static readonly LocKeyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Loc.Instance[parameter as string ?? ""];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
