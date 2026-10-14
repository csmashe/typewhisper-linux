using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Converters;

/// <summary>
///     Formats a <see cref="Profile" />'s app and URL rule counts into the localized
///     <c>Profiles.RulesSummary</c> string for tooltip or accessibility display.
/// </summary>
public sealed class ProfileSummaryConverter : IValueConverter
{
    public static readonly ProfileSummaryConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Profile profile)
        {
            return Loc.Instance.GetString(
                "Profiles.RulesSummary",
                profile.ProcessNames.Count,
                profile.UrlPatterns.Count
            );
        }

        return string.Empty;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => BindingOperations.DoNothing;
}
