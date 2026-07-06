// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
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
// ReSharper disable once UnusedType.Global
public static class PluginLocalizationExtensions
{
    /// <summary>Localizes <paramref name="key" />, or returns it verbatim if no catalog is available.</summary>
    // ReSharper disable once ConvertToExtensionBlock
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static string L(this IPluginLocalization? loc, string key) => loc?.GetString(key) ?? key;

    /// <summary>
    ///     Localizes <paramref name="key" /> and formats it with <paramref name="args" />.
    ///     Falls back to formatting the key itself (then to the raw key on a bad format string).
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static string L(this IPluginLocalization? loc, string key, params object[] args)
    {
        // ReSharper disable once InlineTemporaryVariable
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
