// Non-"unused" inspection kept file-level: the is-checks detect optional-capability interfaces
// implemented by an external/nested plugin type ReSharper can't see, so its "suspicious cast"
// warning is a false positive.
// ReSharper disable SuspiciousTypeConversion.Global
// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
/// Helpers for resolving backward-compatible provider selection IDs.
/// </summary>
// ReSharper disable once UnusedType.Global
public static class PluginSelectionExtensions
{
    /// <summary>
    /// Returns the selection ID for a transcription engine role.
    /// Existing providers default to their plugin ID.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static string GetTranscriptionSelectionId(this ITranscriptionEnginePlugin plugin) =>
        plugin is ITranscriptionEngineSelectionIdentity identity
            && !string.IsNullOrWhiteSpace(identity.TranscriptionSelectionId)
                ? identity.TranscriptionSelectionId
                : plugin.PluginId;

    /// <summary>
    /// Returns the selection ID for an LLM provider role.
    /// Existing providers default to their plugin ID.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static string GetLlmSelectionId(this ILlmProviderPlugin plugin) =>
        plugin is ILlmProviderSelectionIdentity identity
            && !string.IsNullOrWhiteSpace(identity.LlmSelectionId)
                ? identity.LlmSelectionId
                : plugin.PluginId;
}
