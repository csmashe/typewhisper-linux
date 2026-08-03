using System.Diagnostics;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     The host's single compatibility boundary between typed language selection and
///     the nullable-string transcription ABI retained for binary plugin compatibility.
/// </summary>
internal static class TranscriptionLanguageSelectionInvoker
{
    // ReSharper disable once ConvertToExtensionBlock -- the repo has no extension blocks yet; classic `this`-parameter extension methods keep this file consistent with the rest of the codebase.
    public static Task<PluginTranscriptionResult> TranscribeAsync(
        this ITranscriptionEngineRole role,
        byte[] wavAudio,
        LanguageSelection languageSelection,
        bool translate,
        string? prompt,
        CancellationToken ct
    ) =>
        role.TranscribeAsync(
            wavAudio,
            role.ToLegacyLanguage(languageSelection),
            translate,
            prompt,
            ct
        );

    public static Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        this ITranscriptionEngineRole role,
        byte[] wavAudio,
        LanguageSelection languageSelection,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct
    ) =>
        role.TranscribeStreamingAsync(
            wavAudio,
            role.ToLegacyLanguage(languageSelection),
            translate,
            prompt,
            onProgress,
            ct
        );

    public static Task<IStreamingSession> StartStreamingAsync(
        this ITranscriptionEngineRole role,
        LanguageSelection languageSelection,
        CancellationToken ct
    ) => role.StartStreamingAsync(role.ToLegacyLanguage(languageSelection), ct);

    internal static string? ToLegacyLanguage(
        this ITranscriptionEngineRole role,
        LanguageSelection languageSelection
    )
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(languageSelection);

        var capabilities = role as ITranscriptionLanguageSelectionCapabilities;
        var support = languageSelection.IsAutomatic
            ? capabilities?.AutomaticDetectionSupport ?? LanguageSelectionSupport.Unknown
            : capabilities?.ExplicitSelectionSupport ?? LanguageSelectionSupport.Unknown;

        // ReSharper disable once ConvertIfStatementToSwitchStatement -- only the two actionable support values are handled; a switch would need its own missing-enum-cases suppression.
        if (support == LanguageSelectionSupport.Unsupported)
        {
            throw new LanguageSelectionNotSupportedException(
                role.ProviderId,
                role.SelectedModelId,
                languageSelection
            );
        }

        if (support == LanguageSelectionSupport.Unknown)
        {
            Trace.WriteLine(
                $"[LanguageSelection] Provider '{role.ProviderId}' model "
                    + $"'{role.SelectedModelId ?? "<unknown>"}' did not advertise "
                    + $"{(languageSelection.IsAutomatic ? "automatic-detection" : "explicit-selection")} support; preserving the legacy ABI behavior."
            );
        }

        return languageSelection.IsAutomatic ? null : languageSelection.LanguageTag;
    }
}

internal static class LanguageSelectionResolver
{
    /// <summary>
    ///     Resolves raw values in precedence order. Blank values mean "no override";
    ///     if every value is blank, automatic selection is used.
    /// </summary>
    public static LanguageSelection Resolve(params string?[] values)
    {
        var rawValue = values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (rawValue is null)
        {
            return LanguageSelection.Automatic;
        }

        return LanguageSelection.TryParse(rawValue, out var selection)
            ? selection
            : throw new InvalidLanguageSelectionException(rawValue);
    }

    /// <summary>
    ///     <see cref="Resolve" /> for bookkeeping paths that must not fail on an
    ///     unparsable value; an invalid value degrades to automatic selection.
    /// </summary>
    public static LanguageSelection ResolveOrAutomatic(params string?[] values)
    {
        try
        {
            return Resolve(values);
        }
        catch (InvalidLanguageSelectionException)
        {
            return LanguageSelection.Automatic;
        }
    }
}

internal sealed class InvalidLanguageSelectionException(string rawValue)
    : FormatException($"Invalid transcription language selection '{rawValue.Trim()}'. Use 'auto' or a valid BCP-47 tag.")
{
    public string RawValue { get; } = rawValue;
}

internal static class LanguageSelectionUiMessage
{
    public static string From(Exception exception) =>
        exception switch
        {
            InvalidLanguageSelectionException invalid =>
                Loc.Instance.GetString(
                    "LanguageSelection.Invalid",
                    invalid.RawValue.Trim()
                ),
            LanguageSelectionNotSupportedException { Selection.IsAutomatic: true } unsupported =>
                Loc.Instance.GetString(
                    "LanguageSelection.AutomaticNotSupported",
                    unsupported.ProviderId
                ),
            LanguageSelectionNotSupportedException unsupported =>
                Loc.Instance.GetString(
                    "LanguageSelection.ExplicitNotSupported",
                    unsupported.ProviderId
                ),
            _ => exception.Message,
        };
}
