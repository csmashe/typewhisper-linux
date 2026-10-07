// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
///     Static helper for Whisper-compatible audio transcription API calls.
///     Shared by transcription engine plugins targeting OpenAI's API shape.
/// </summary>
// ReSharper disable once UnusedType.Global
public static class OpenAiTranscriptionHelper
{
    /// <inheritdoc cref="TranscribeAsync(HttpClient, string, string, string, byte[], string?, bool, string, string?, Uri?, IReadOnlyDictionary{string, string}?, CancellationToken)" />
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        byte[] wavAudio,
        string? language,
        bool translate,
        string responseFormat,
        CancellationToken ct,
        string? prompt = null
    ) =>
        // Kept binary-compatible: plugins compiled against the older SDK still bind here.
        TranscribeAsync(httpClient, baseUrl, apiKey, model, wavAudio, language, translate,
            responseFormat, prompt, endpointOverride: null, requestHeaders: null, ct);

    /// <summary>
    ///     Sends a transcription request to a Whisper-compatible API endpoint.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="baseUrl">API base URL (e.g. "https://api.openai.com").</param>
    /// <param name="apiKey">Bearer token for authentication.</param>
    /// <param name="model">Model identifier (e.g. "whisper-1").</param>
    /// <param name="wavAudio">WAV-encoded audio bytes.</param>
    /// <param name="language">
    ///     Language hint (ISO code). Null, blank, or the <c>"auto"</c> sentinel omits the field
    ///     so the provider detects the language itself. The field is omitted on translation requests.
    /// </param>
    /// <param name="translate">If true, uses the translations endpoint (audio to English).</param>
    /// <param name="responseFormat">
    ///     Response format. Supported values are <c>"verbose_json"</c>, <c>"json"</c>,
    ///     and <c>"text"</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="prompt">Optional text to bias the model toward specific spelling, vocabulary, or style; null to omit.</param>
    /// <param name="endpointOverride">Explicit endpoint, already selected for transcription or translation; null builds the standard route.</param>
    /// <param name="requestHeaders">Extra headers; a supplied Authorization replaces apiKey bearer authentication.</param>
    /// <returns>
    ///     Transcription result with text, detected language, and duration. The <c>"text"</c>
    ///     format supplies only text, so language, duration, and segments use their default values.
    /// </returns>
    // ReSharper disable once UnusedMember.Global
    public static async Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        byte[] wavAudio,
        string? language,
        bool translate,
        string responseFormat,
        string? prompt,
        Uri? endpointOverride,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken ct
    )
    {
        var parseAsPlainText = responseFormat switch
        {
            "text" => true,
            "json" or "verbose_json" => false,
            _ => throw new ArgumentException(
                $"Unsupported transcription response format: '{responseFormat}'. "
                + "Supported formats are 'verbose_json', 'json', and 'text'.",
                nameof(responseFormat)
            ),
        };

        var endpoint = translate
            ? $"{baseUrl}/v1/audio/translations"
            : $"{baseUrl}/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(responseFormat), "response_format");

        // "auto" is a sentinel, not a language code: Whisper-compatible endpoints reject it.
        var languageHint = language?.Trim();
        // Translation targets English; Whisper-style translation endpoints accept no source-language hint.
        if (
            !translate
            && !string.IsNullOrEmpty(languageHint)
            && !languageHint.Equals("auto", StringComparison.OrdinalIgnoreCase)
        )
        {
            content.Add(new StringContent(languageHint), "language");
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            content.Add(new StringContent(prompt), "prompt");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointOverride ?? new Uri(endpoint, UriKind.RelativeOrAbsolute));
        if (requestHeaders?.Keys.Any(name => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) != true)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (requestHeaders is not null)
            foreach (var (name, value) in requestHeaders)
                request.Headers.TryAddWithoutValidation(name, value);
        request.Content = content;

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return parseAsPlainText
            ? ParsePlainTextTranscriptionResponse(responseBody)
            : ParseTranscriptionResponse(responseBody);
    }

    private static PluginTranscriptionResult ParsePlainTextTranscriptionResponse(string responseBody)
    {
        var text = responseBody.EndsWith("\r\n", StringComparison.Ordinal)
            ? responseBody[..^2]
            : responseBody.EndsWith('\n') || responseBody.EndsWith('\r')
                ? responseBody[..^1]
                : responseBody;
        return new PluginTranscriptionResult(text, null, 0, null);
    }

    /// <summary>
    ///     Parses a Whisper-compatible JSON transcription response.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    internal static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("text", out var textEl)
            || textEl.ValueKind != JsonValueKind.String)
        {
            throw CreateInvalidResponseException(json, root);
        }

        var text = textEl.GetString() ?? "";
        // Only "text" is required; malformed optional metadata is dropped, never the transcript.
        var language = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
            ? langEl.GetString()
            : null;
        var duration = ReadNumber(root, "duration") ?? 0;
        var segments = new List<PluginTranscriptionSegment>();

        // Use min no_speech_prob so the silence filter only triggers when ALL segments are silence.
        float? minNoSpeechProb = null;
        if (!root.TryGetProperty("segments", out var segmentsEl)
            || segmentsEl.ValueKind != JsonValueKind.Array)
        {
            return new PluginTranscriptionResult(text.Trim(), language, duration, minNoSpeechProb)
            {
                Segments = segments,
            };
        }

        // An unrated segment that may hold speech makes all-segment silence unknown.
        var hasUnratedSpeech = false;
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- only the guard converts; the body mutates state and LINQ would box the JsonElement.ArrayEnumerator.
        foreach (var seg in segmentsEl.EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Object)
                continue;
            var hasStringText = seg.TryGetProperty("text", out var segTextEl) && segTextEl.ValueKind == JsonValueKind.String;
            var segmentText = hasStringText ? segTextEl.GetString() ?? "" : "";
            var start = ReadNumber(seg, "start") ?? 0;
            var end = ReadNumber(seg, "end") ?? 0;
            var prob = ReadNumber(seg, "no_speech_prob") is { } noSpeech and >= 0 and <= 1
                ? (float?)noSpeech
                : null;
            segments.Add(new PluginTranscriptionSegment(segmentText, start, end)
            {
                NoSpeechProbability = prob,
            });
            if (prob is { } probability)
            {
                minNoSpeechProb = minNoSpeechProb is null
                    ? probability
                    : Math.Min(minNoSpeechProb.Value, probability);
            }
            else if (!hasStringText || !string.IsNullOrWhiteSpace(segmentText))
            {
                hasUnratedSpeech = true;
            }
        }

        return new PluginTranscriptionResult(text.Trim(), language, duration, hasUnratedSpeech ? null : minNoSpeechProb)
        {
            Segments = segments,
        };
    }

    // Some servers (e.g. GPUStack) send verbose_json numbers as strings such as "2.49".
    private static double? ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var number) ? number : (double?)null,
            JsonValueKind.String => double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) ? number : null,
            _ => null,
        };
        return parsed is { } finite && double.IsFinite(finite) ? finite : null;
    }

    private static InvalidOperationException CreateInvalidResponseException(
        string json,
        JsonElement root
    )
    {
        var providerError = TryGetProviderErrorMessage(root);
        var providerErrorDetail = providerError is null
            ? ""
            : $" Provider error: {providerError}";
        return new InvalidOperationException(
            "Invalid transcription response: required field 'text' must be a string."
            + $"{providerErrorDetail} Body: {GetBodySnippet(json)}"
        );
    }

    private static string? TryGetProviderErrorMessage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return null;
    }

    private static string GetBodySnippet(string json)
    {
        const int maxLength = 200;
        return json.Length > maxLength ? $"{json[..maxLength]}..." : json;
    }
}
