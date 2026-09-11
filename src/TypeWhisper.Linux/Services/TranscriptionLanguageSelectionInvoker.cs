using System.Diagnostics;
using TypeWhisper.Core.Models;
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

    // The hints overloads below hand a plugin every usable hint when there is more than one and
    // otherwise make exactly the single-language call the plugin has always received.
    public static Task<PluginTranscriptionResult> TranscribeAsync(
        this ITranscriptionEngineRole role,
        byte[] wavAudio,
        LanguageSelection languageSelection,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        var hints = role.ToLanguageHints(languageSelection, languageHints);
        return UseSingleLanguageCall(languageSelection, hints)
            ? role.TranscribeAsync(wavAudio, hints.Count == 0 ? null : hints[0], translate, prompt, ct)
            : role.TranscribeWithLanguageHintsAsync(wavAudio, hints, translate, prompt, ct);
    }

    public static Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        this ITranscriptionEngineRole role,
        byte[] wavAudio,
        LanguageSelection languageSelection,
        IReadOnlyList<string> languageHints,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct
    )
    {
        var hints = role.ToLanguageHints(languageSelection, languageHints);
        return UseSingleLanguageCall(languageSelection, hints)
            ? role.TranscribeStreamingAsync(
                wavAudio,
                hints.Count == 0 ? null : hints[0],
                translate,
                prompt,
                onProgress,
                ct
            )
            : role.TranscribeStreamingWithLanguageHintsAsync(wavAudio, hints, translate, prompt, onProgress, ct);
    }

    public static Task<IStreamingSession> StartStreamingAsync(
        this ITranscriptionEngineRole role,
        LanguageSelection languageSelection,
        IReadOnlyList<string> languageHints,
        CancellationToken ct
    )
    {
        var hints = role.ToLanguageHints(languageSelection, languageHints);
        return UseSingleLanguageCall(languageSelection, hints)
            ? role.StartStreamingAsync(hints.Count == 0 ? null : hints[0], ct)
            : role.StartStreamingWithLanguageHintsAsync(hints, ct);
    }

    // An advisory hint under automatic detection must reach the engine as a hint, never as the
    // single-language argument, which would force it.
    private static bool UseSingleLanguageCall(LanguageSelection languageSelection, List<string> hints) =>
        hints.Count == 0 || (hints.Count == 1 && !languageSelection.IsAutomatic);

    // ReSharper disable once ConvertToExtensionBlock -- see the note on the first method above.
    private static List<string> ToLanguageHints(
        this ITranscriptionEngineRole role,
        LanguageSelection languageSelection,
        IReadOnlyList<string> languageHints
    )
    {
        var primary = role.ToLegacyLanguage(languageSelection);
        // Advisory hints under automatic detection only go to engines that take them as hints.
        if (primary is null && !role.SupportsLanguageHints)
        {
            return [];
        }

        var hints = primary is null ? [] : new List<string> { primary };
        // Only the primary is a hard requirement (validated above); extras are best-effort, so an
        // unparsable, duplicate or unsupported extra is dropped rather than failing the run.
        foreach (var hint in languageHints)
        {
            if (LanguageSelection.TryParse(hint, out var selection)
                && !selection.IsAutomatic
                && !hints.Contains(selection.LanguageTag!, StringComparer.OrdinalIgnoreCase)
                && (role.SupportedLanguages.Count == 0
                    || role.SupportedLanguages.Contains(selection.LanguageTag!, StringComparer.OrdinalIgnoreCase)))
            {
                hints.Add(selection.LanguageTag!);
            }
        }

        return hints;
    }

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

        if (
            !languageSelection.IsAutomatic
            && role.SupportedLanguages is { Count: > 0 } supportedLanguages
            && !supportedLanguages.Contains(
                languageSelection.LanguageTag!,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            throw new TranscriptionLanguageNotSupportedException(
                role.ProviderId,
                role.SelectedModelId,
                languageSelection,
                supportedLanguages
            );
        }

        return languageSelection.IsAutomatic ? null : languageSelection.LanguageTag;
    }
}

internal sealed class TranscriptionLanguageNotSupportedException : NotSupportedException
{
    public TranscriptionLanguageNotSupportedException(
        string providerId,
        string? modelId,
        LanguageSelection selection,
        IReadOnlyList<string> supportedLanguages
    )
        : base(
            $"Transcription provider '{providerId}' model '{modelId ?? "<unknown>"}' "
                + $"does not support language '{selection.LanguageTag}'. Supported languages: "
                + $"{string.Join(", ", supportedLanguages)}."
        )
    {
        ProviderId = providerId;
        ModelId = modelId;
        Selection = selection;
        SupportedLanguages = [.. supportedLanguages];
    }

    public string ProviderId { get; }
    public string? ModelId { get; }
    public LanguageSelection Selection { get; }
    public IReadOnlyList<string> SupportedLanguages { get; }
}

internal static class LanguageSelectionResolver
{
    /// <summary>Ordered hints for a run: the profile's view of the global hints, or the global hints alone.</summary>
    public static IReadOnlyList<string> ResolveHints(Profile? profile, AppSettings settings) =>
        profile?.GetLanguageHints(settings.GetLanguageHints()) ?? settings.GetLanguageHints();

    /// <summary>The primary selection for an ordered hint list: its first entry, or automatic when empty.</summary>
    public static LanguageSelection ResolvePrimary(IReadOnlyList<string> languageHints) =>
        Resolve(languageHints.Count == 0 ? null : languageHints[0]);

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
            TranscriptionLanguageNotSupportedException unsupported =>
                Loc.Instance.GetString(
                    "LanguageSelection.LanguageNotSupported",
                    unsupported.ProviderId,
                    unsupported.Selection.LanguageTag ?? string.Empty,
                    string.Join(", ", unsupported.SupportedLanguages)
                ),
            _ => exception.Message,
        };
}
