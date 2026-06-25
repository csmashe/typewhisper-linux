// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional interface for plugins that localize their settings UI. The host
///     injects an <see cref="IPluginLocalization" /> at load time — before and
///     independent of activation — so settings metadata (labels, descriptions,
///     validation messages) resolves even for plugins the user has not enabled
///     yet. Without it, localization would only be available after activation
///     (which only happens for enabled plugins), so a disabled plugin's settings
///     panel would render raw keys like <c>Settings.ApiKey</c>.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IPluginLocalizationAware
{
    /// <summary>Supplies the plugin's localization catalog. Called once at load, before activation.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    void SetLocalization(IPluginLocalization localization);
}
