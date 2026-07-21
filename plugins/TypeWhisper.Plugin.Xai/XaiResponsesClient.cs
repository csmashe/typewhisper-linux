using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.Xai;

internal sealed class XaiResponsesClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public XaiResponsesClient(HttpClient httpClient, string baseUrl, string apiKey)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct)
    {
        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = XaiJson.Element(model),
            ["store"] = XaiJson.Element(false),
            ["input"] = XaiJson.Element(new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText },
            }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = XaiJson.CreateJsonContent(body);

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(json);
    }

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = XaiJson.Element(model),
            ["store"] = XaiJson.Element(false),
            ["stream"] = XaiJson.Element(true),
            ["input"] = XaiJson.Element(new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText },
            }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = XaiJson.CreateJsonContent(body);

        // ResponseHeadersRead so deltas surface as they arrive instead of
        // buffering the whole SSE body (the batch path's SendWithErrorHandlingAsync
        // buffers, so we send + check the status line ourselves here).
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var message = (int)response.StatusCode switch
            {
                401 => "Invalid API key",
                429 => "Rate limit reached, please wait",
                _ => $"API error {(int)response.StatusCode}: {OpenAiApiHelper.ExtractErrorMessage(errorBody)}",
            };
            throw new InvalidOperationException(message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } rawLine)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var payload = line[6..];
            if (payload == "[DONE]")
                yield break;

            // The Responses stream returns 200 before generation finishes, so a
            // mid-stream failure arrives as a typed `error` / `response.failed`
            // frame rather than an HTTP error. Throw on those so the pump faults
            // and the caller falls back to batch, instead of silently committing
            // the partial deltas seen so far as a successful result.
            if (ParseStreamError(payload) is { } error)
                throw new InvalidOperationException(error);

            if (ParseStreamDelta(payload) is { Length: > 0 } delta)
                yield return delta;
        }
    }

    /// <summary>
    ///     Extracts the incremental text from a single xAI Responses SSE
    ///     <c>data:</c> payload — a <c>response.output_text.delta</c> frame's
    ///     <c>delta</c> string. Returns <c>null</c> for any other frame type or an
    ///     unparseable payload. Reflection-free (A18) via <see cref="JsonDocument" />.
    /// </summary>
    internal static string? ParseStreamDelta(string dataPayload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && typeEl.GetString() == "response.output_text.delta"
                && root.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.String)
            {
                return delta.GetString();
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns a provider error message when a single Responses SSE
    ///     <c>data:</c> payload is a failure frame — a top-level <c>error</c> event
    ///     or a <c>response.failed</c> lifecycle frame — otherwise <c>null</c>.
    ///     Used by the streaming reader to surface a post-200 stream failure as a
    ///     thrown exception. Reflection-free (A18) via <see cref="JsonDocument" />.
    /// </summary>
    internal static string? ParseStreamError(string dataPayload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            // ReSharper disable once ConvertSwitchStatementToSwitchExpression -- subjective style; the statement switch reads fine here.
            switch (typeEl.GetString())
            {
                case "error":
                    return ExtractErrorMessage(root) ?? "xAI streaming error.";
                case "response.failed":
                    return root.TryGetProperty("response", out var resp)
                        && resp.ValueKind == JsonValueKind.Object
                        ? ExtractErrorMessage(resp) ?? "xAI response failed."
                        : "xAI response failed.";
                default:
                    return null;
            }
        }
    }

    private static string? ExtractErrorMessage(JsonElement element)
    {
        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (element.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString();
            }

            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        return element.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;
    }

    public static string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (TryGetNonEmptyString(root, "output_text") is { } outputText)
            return outputText;

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- explicit loop kept; the LINQ form switches enumerators and obscures the side effects.
                foreach (var contentItem in content.EnumerateArray())
                {
                    var type = TryGetNonEmptyString(contentItem, "type");
                    if (type is not null
                        && type != "output_text"
                        && type != "text")
                    {
                        continue;
                    }

                    if (TryGetNonEmptyString(contentItem, "text") is { } text)
                        parts.Add(text);
                }
            }

            var nestedText = JoinTextParts(parts);
            if (!string.IsNullOrWhiteSpace(nestedText))
                return nestedText;
        }

        throw new InvalidOperationException("Failed to parse xAI response text.");
    }

    private static string JoinTextParts(IReadOnlyList<string> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts.Where(static part => !string.IsNullOrEmpty(part)))
        {
            if (builder.Length > 0
                && !char.IsWhiteSpace(builder[^1])
                && !char.IsWhiteSpace(part[0])
                && !char.IsPunctuation(part[0]))
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        return builder.ToString().Trim();
    }

    private static string? TryGetNonEmptyString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
