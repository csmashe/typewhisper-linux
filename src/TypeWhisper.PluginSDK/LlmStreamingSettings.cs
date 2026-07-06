// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Shared keys for LLM streaming plugin settings, referenced by every LLM
///     provider plugin so the per-provider "stream responses" toggle key never
///     drifts across plugins.
/// </summary>
// ReSharper disable once UnusedType.Global
public static class LlmStreamingSettings
{
    /// <summary>
    ///     Boolean plugin-setting key for the per-provider streaming toggle. When
    ///     unset the behavior is ON (opt-out) — see the C7 master plan.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    public const string StreamResponsesSettingKey = "streamResponses";
}
