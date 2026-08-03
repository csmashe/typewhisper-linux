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
    private const string InvalidSelectionIdMessage =
        "Effective selection IDs must be non-empty and contain only ASCII letters, "
        + "ASCII digits, dots, dashes, and underscores ([A-Za-z0-9._-]+).";

    /// <summary>
    /// Returns the selection ID for a transcription engine role.
    /// Existing providers and empty or whitespace-only custom identities default to their
    /// plugin ID.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The effective selection ID does not match <c>[A-Za-z0-9._-]+</c>.
    /// </exception>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static string GetTranscriptionSelectionId(this ITranscriptionEngineRole plugin)
    {
        var customSelectionId = plugin is ITranscriptionEngineSelectionIdentity identity
            ? identity.TranscriptionSelectionId
            : null;
        var selectionId = string.IsNullOrWhiteSpace(customSelectionId)
            ? plugin.PluginId
            : customSelectionId;
        return ValidateSelectionId(selectionId);
    }

    /// <summary>
    /// Returns the selection ID for an LLM provider role.
    /// Existing providers and empty or whitespace-only custom identities default to their
    /// plugin ID.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The effective selection ID does not match <c>[A-Za-z0-9._-]+</c>.
    /// </exception>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static string GetLlmSelectionId(this ILlmProviderRole plugin)
    {
        var customSelectionId = plugin is ILlmProviderSelectionIdentity identity
            ? identity.LlmSelectionId
            : null;
        var selectionId = string.IsNullOrWhiteSpace(customSelectionId)
            ? plugin.PluginId
            : customSelectionId;
        return ValidateSelectionId(selectionId);
    }

    /// <summary>
    /// Returns whether an effective selection ID matches <c>[A-Za-z0-9._-]+</c>.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once MemberCanBePrivate.Global -- public plugin-SDK surface; external plugin authors call it to pre-validate custom selection IDs against the Get* methods' documented contract.
    public static bool IsValidSelectionId(string? selectionId)
    {
        if (string.IsNullOrEmpty(selectionId))
        {
            return false;
        }

        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- LINQ would swap the struct enumerator for the boxing interface one in a hot path.
        foreach (var character in selectionId)
        {
            if (
                character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '.'
                and not '-'
                and not '_'
            )
            {
                return false;
            }
        }

        return true;
    }

    private static string ValidateSelectionId(string selectionId)
    {
        return IsValidSelectionId(selectionId)
            ? selectionId
            : throw new InvalidOperationException(InvalidSelectionIdMessage);
    }
}
