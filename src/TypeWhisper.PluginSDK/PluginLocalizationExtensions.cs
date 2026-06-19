using System.Globalization;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Null-safe convenience helpers for localizing plugin-facing strings.
///     Plugins expose a <c>Loc</c> property (usually <c>_host?.Localization</c>)
///     and call <c>Loc.L("Settings.Key")</c>. When the host has not supplied an
///     <see cref="IPluginLocalization" /> (e.g. before activation or in unit
///     tests) the key is returned unchanged, mirroring
///     <see cref="IPluginLocalization.GetString(string)" />'s own fallback.
/// </summary>
public static class PluginLocalizationExtensions
{
    /// <summary>Localizes <paramref name="key" />, or returns it verbatim if no catalog is available.</summary>
    public static string L(this IPluginLocalization? loc, string key) => loc?.GetString(key) ?? key;

    /// <summary>
    ///     Localizes <paramref name="key" /> and formats it with <paramref name="args" />.
    ///     Falls back to formatting the key itself (then to the raw key on a bad format string).
    /// </summary>
    public static string L(this IPluginLocalization? loc, string key, params object[] args)
    {
        if (loc is { } available)
        {
            return available.GetString(key, args);
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, key, args);
        }
        catch (FormatException)
        {
            return key;
        }
    }
}
