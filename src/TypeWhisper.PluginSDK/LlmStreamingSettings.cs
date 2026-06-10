namespace TypeWhisper.PluginSDK;

/// <summary>
///     Shared keys for LLM streaming plugin settings, referenced by every LLM
///     provider plugin so the per-provider "stream responses" toggle key never
///     drifts across plugins.
/// </summary>
public static class LlmStreamingSettings
{
    /// <summary>
    ///     Boolean plugin-setting key for the per-provider streaming toggle. When
    ///     unset the behavior is ON (opt-out) — see the C7 master plan.
    /// </summary>
    public const string StreamResponsesSettingKey = "streamResponses";
}