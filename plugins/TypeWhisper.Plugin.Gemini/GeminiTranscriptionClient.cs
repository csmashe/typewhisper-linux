using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Gemini;

internal static class GeminiTranscriptionClient
{
    private const string AudioMimeType = "audio/wav";

    // Uploads the WAV, runs a stateless transcription interaction over it, then removes the upload.
    public static async Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string modelId,
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        IReadOnlyList<string> customVocabulary,
        GeminiTranscriptionMode mode,
        Action<PluginLogLevel, string>? log,
        CancellationToken ct)
    {
        if (wavAudio.Length == 0)
        {
            throw new PluginRequestException(
                "Audio is empty.",
                PluginRequestFailureKind.InvalidRequest);
        }

        // Captured as soon as the API names the file, so an upload it accepted but described
        // incompletely is still reclaimed below.
        string? uploadedFileName = null;
        try
        {
            var fileUri = await UploadAudioAsync(
                httpClient,
                baseUrl,
                apiKey,
                wavAudio,
                name => uploadedFileName = name,
                ct);
            using var request = GeminiPlugin.CreateNativeRequest(
                HttpMethod.Post,
                $"{baseUrl}/interactions",
                apiKey);
            request.Content = new StringContent(
                CreateInteractionPayload(
                    modelId,
                    fileUri,
                    languageHints,
                    customVocabulary,
                    mode),
                Encoding.UTF8,
                "application/json");

            using var response = await SendAsync(httpClient, request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            // A hint is not a detection, so no language is claimed: under multi-hint automatic
            // detection the first hint is frequently not the language that was spoken.
            return new PluginTranscriptionResult(
                ParseInteractionText(json),
                DetectedLanguage: null,
                CalculateWavDurationSeconds(wavAudio));
        }
        finally
        {
            if (uploadedFileName is not null)
                await DeleteUploadBestEffortAsync(httpClient, baseUrl, apiKey, uploadedFileName, log);
        }
    }

    internal static string CreateInteractionPayload(
        string modelId,
        string fileUri,
        IReadOnlyList<string> languageHints,
        IReadOnlyList<string> customVocabulary,
        GeminiTranscriptionMode mode)
    {
        var transcriptionConfig = new Dictionary<string, object?>
        {
            // An empty list is how the API is told to detect the language itself.
            ["language_codes"] = languageHints,
            ["mode"] = mode == GeminiTranscriptionMode.Smart
                ? "smart"
                : new Dictionary<string, object?> { ["type"] = "verbatim" },
        };
        if (customVocabulary.Count > 0)
            transcriptionConfig["custom_vocabulary"] = customVocabulary;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "audio",
                    ["uri"] = fileUri,
                    ["mime_type"] = AudioMimeType,
                },
            },
            ["generation_config"] = new Dictionary<string, object?>
            {
                ["transcription_config"] = transcriptionConfig,
            },
            ["store"] = false,
        };

        return JsonSerializer.Serialize(payload);
    }

    internal static string ParseInteractionText(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (TryGetProperty(root, "status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.String
                && statusElement.GetString() is { } status
                && !status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                var failureKind = status.Equals("incomplete", StringComparison.OrdinalIgnoreCase)
                    ? PluginRequestFailureKind.OutputIncomplete
                    : PluginRequestFailureKind.ServerError;
                throw new PluginRequestException(
                    $"Gemini transcription interaction ended with status '{status}'.",
                    failureKind);
            }

            if (!TryGetProperty(root, "steps", out var steps)
                || steps.ValueKind != JsonValueKind.Array)
            {
                throw new PluginRequestException(
                    "Gemini returned a transcription response without output steps.",
                    PluginRequestFailureKind.EmptyResponse);
            }

            foreach (var step in steps.EnumerateArray().Reverse())
            {
                if (!TryGetString(step, "type", out var type)
                    || !type.Equals("model_output", StringComparison.OrdinalIgnoreCase)
                    || !TryGetProperty(step, "content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var parts = content
                    .EnumerateArray()
                    .Where(part => TryGetString(part, "type", out var contentType)
                        && contentType.Equals("text", StringComparison.OrdinalIgnoreCase))
                    .Select(part => TryGetString(part, "text", out var text) ? text : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();
                if (parts.Count > 0)
                    return string.Concat(parts).Trim();
            }
        }
        catch (JsonException ex)
        {
            throw new PluginRequestException(
                "Gemini returned a malformed transcription response.",
                PluginRequestFailureKind.EmptyResponse,
                innerException: ex);
        }

        throw new PluginRequestException(
            "Gemini returned an empty transcription.",
            PluginRequestFailureKind.EmptyResponse);
    }

    // Walks the WAV chunk table rather than trusting the response, and bails out on any chunk
    // size that would run past the buffer so a truncated or hostile header cannot loop forever.
    internal static double CalculateWavDurationSeconds(byte[] wavAudio)
    {
        if (wavAudio.Length < 12
            || Encoding.ASCII.GetString(wavAudio, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(wavAudio, 8, 4) != "WAVE")
        {
            return 0;
        }

        var byteRate = 0;
        var dataSize = 0;
        for (var offset = 12; offset <= wavAudio.Length - 8;)
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wavAudio.AsSpan(offset + 4, 4));
            if (chunkSize < 0)
                break;

            var dataStart = offset + 8;
            switch (chunkId)
            {
                case "fmt " when chunkSize >= 12 && dataStart <= wavAudio.Length - 12:
                    byteRate = BinaryPrimitives.ReadInt32LittleEndian(
                        wavAudio.AsSpan(dataStart + 8, 4));
                    break;
                case "data":
                    dataSize = Math.Min(chunkSize, Math.Max(0, wavAudio.Length - dataStart));
                    break;
            }

            var paddedChunkSize = (long)chunkSize + (chunkSize & 1);
            if (paddedChunkSize > wavAudio.Length - (long)dataStart)
                break;
            offset = dataStart + (int)paddedChunkSize;
        }

        return byteRate > 0 && dataSize > 0
            ? dataSize / (double)byteRate
            : 0;
    }

    private static async Task<string> UploadAudioAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        byte[] wavAudio,
        Action<string> onFileNameParsed,
        CancellationToken ct)
    {
        var uploadBaseUrl = baseUrl.Replace(
            "/v1beta",
            "/upload/v1beta",
            StringComparison.OrdinalIgnoreCase);
        using var startRequest = GeminiPlugin.CreateNativeRequest(
            HttpMethod.Post,
            $"{uploadBaseUrl}/files",
            apiKey);
        startRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol", "resumable");
        startRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "start");
        startRequest.Headers.TryAddWithoutValidation(
            "X-Goog-Upload-Header-Content-Length",
            wavAudio.Length.ToString(CultureInfo.InvariantCulture));
        startRequest.Headers.TryAddWithoutValidation(
            "X-Goog-Upload-Header-Content-Type",
            AudioMimeType);
        startRequest.Content = new StringContent(
            """{"file":{"display_name":"typewhisper-audio.wav"}}""",
            Encoding.UTF8,
            "application/json");

        using var startResponse = await SendAsync(httpClient, startRequest, ct);
        if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls)
            || uploadUrls.FirstOrDefault() is not { } uploadUrl
            || string.IsNullOrWhiteSpace(uploadUrl))
        {
            throw new PluginRequestException(
                "Gemini did not return an upload URL.",
                PluginRequestFailureKind.EmptyResponse);
        }

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset", "0");
        uploadRequest.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
        uploadRequest.Content = new ByteArrayContent(wavAudio);
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(AudioMimeType);

        using var uploadResponse = await SendAsync(httpClient, uploadRequest, ct);
        var json = await uploadResponse.Content.ReadAsStringAsync(ct);
        return ParseUploadedFileUri(json, onFileNameParsed);
    }

    private static string ParseUploadedFileUri(string json, Action<string> onFileNameParsed)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            // ReSharper disable once InvertIf -- the positive form lets a missing name and a
            // missing uri fall through to the one throw below; inverting would duplicate it.
            if (TryGetProperty(document.RootElement, "file", out var file)
                && TryGetString(file, "name", out var name)
                && !string.IsNullOrWhiteSpace(name))
            {
                onFileNameParsed(name);
                if (TryGetString(file, "uri", out var uri) && !string.IsNullOrWhiteSpace(uri))
                    return uri;
            }

            throw new PluginRequestException(
                "Gemini returned incomplete uploaded-file metadata.",
                PluginRequestFailureKind.EmptyResponse);
        }
        catch (JsonException ex)
        {
            throw new PluginRequestException(
                "Gemini returned malformed uploaded-file metadata.",
                PluginRequestFailureKind.EmptyResponse,
                innerException: ex);
        }
    }

    // Its own budget: the caller's token is usually already cancelled or spent by the time the
    // upload has to be reclaimed, and a failed cleanup must never mask the transcription result.
    private static async Task DeleteUploadBestEffortAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string fileName,
        Action<PluginLogLevel, string>? log)
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var request = GeminiPlugin.CreateNativeRequest(
                HttpMethod.Delete,
                $"{baseUrl}/{fileName.TrimStart('/')}",
                apiKey);
            using var response = await httpClient.SendAsync(request, cleanupCts.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                log?.Invoke(
                    PluginLogLevel.Warning,
                    $"Could not delete Gemini audio upload (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            log?.Invoke(
                PluginLogLevel.Warning,
                $"Could not delete Gemini audio upload ({ex.GetType().Name}).");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException(
                $"Network error: {ex.Message}",
                PluginRequestFailureKind.Network,
                innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                "Gemini API request timed out.",
                PluginRequestFailureKind.Timeout,
                innerException: ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAt)
                retryAfter = retryAt - DateTimeOffset.UtcNow;

            var errorBody = await response.Content.ReadAsStringAsync(ct);

            var failureKind = statusCode switch
            {
                401 => PluginRequestFailureKind.Authentication,
                403 => PluginRequestFailureKind.Permission,
                408 => PluginRequestFailureKind.Timeout,
                413 => PluginRequestFailureKind.RequestTooLarge,
                429 => PluginRequestFailureKind.RateLimit,
                >= 500 and <= 599 => PluginRequestFailureKind.ServerError,
                >= 400 and <= 499 => PluginRequestFailureKind.InvalidRequest,
                _ => PluginRequestFailureKind.Unknown,
            };
            var message = statusCode switch
            {
                401 => "Invalid Gemini API key",
                429 => "Gemini rate limit reached, please wait",
                _ => $"Gemini API error {statusCode}: {OpenAiApiHelper.ExtractErrorMessage(errorBody)}",
            };
            throw new PluginRequestException(message, failureKind, statusCode, retryAfter);
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!TryGetProperty(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return true;
    }

    // A null or scalar where the API documents an object makes JsonElement.TryGetProperty throw,
    // and that would escape these parsers as a raw InvalidOperationException.
    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value);
    }
}
