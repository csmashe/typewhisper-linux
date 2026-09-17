// Shared between the OpenAI and OpenAI Compatible plugins and exercised by their tests, so the
// analyzer cannot see every caller of these members; MemberCanBePrivate misfires here.
// ReSharper disable MemberCanBePrivate.Global
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugins.Shared.OpenAi;

internal sealed class OpenAiResponsesClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public OpenAiResponsesClient(HttpClient httpClient, Uri endpoint, IReadOnlyDictionary<string, string> headers)
    {
        _httpClient = httpClient;
        _endpoint = endpoint;
        _headers = headers;
    }

    // ReSharper disable once UnusedMember.Global -- used by the OpenAI plugin; this file is linked into both plugins.
    public OpenAiResponsesClient(HttpClient httpClient, string baseUrl, string apiKey)
        : this(httpClient, new Uri(baseUrl.TrimEnd('/') + "/v1/responses"),
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" })
    {
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        string? reasoningEffort,
        CancellationToken ct,
        double? temperature = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        foreach (var (name, value) in _headers)
            request.Headers.TryAddWithoutValidation(name, value);
        request.Content = OpenAiJson.CreateJsonContent(
            CreateRequestBody(model, systemPrompt, userText, reasoningEffort, temperature));

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(json);
    }

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string model,
        string systemPrompt,
        string userText,
        string? reasoningEffort,
        double? temperature = null)
    {
        var instructions = string.IsNullOrWhiteSpace(systemPrompt)
            ? "You are a helpful assistant."
            : systemPrompt;

        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = OpenAiJson.Element(model),
            ["max_output_tokens"] = OpenAiJson.Element(!string.IsNullOrWhiteSpace(reasoningEffort)
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText)),
            ["instructions"] = OpenAiJson.Element(instructions),
            ["input"] = OpenAiJson.Element(new[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_text", text = userText },
                    },
                },
            }),
            ["store"] = OpenAiJson.Element(false),
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning"] = OpenAiJson.Element(new { effort = reasoningEffort });

        if (temperature is not null)
            body["temperature"] = OpenAiJson.Element(temperature.Value);

        return body;
    }

    internal static string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && status.GetString() is "failed" or "cancelled")
        {
            throw new PluginRequestException("OpenAI response did not complete.", PluginRequestFailureKind.OutputIncomplete);
        }
        LlmResponseTruncationGuard.ThrowIfResponsesApiIncomplete(root, "OpenAI");

        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            var text = outputText.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    var type = contentItem.TryGetProperty("type", out var typeEl)
                        ? typeEl.GetString()
                        : null;
                    if (type is not null and not "output_text" and not "text")
                        continue;

                    if (!contentItem.TryGetProperty("text", out var textEl))
                        continue;
                    if (textEl.ValueKind == JsonValueKind.Object
                        && textEl.TryGetProperty("value", out var value))
                        textEl = value;
                    if (textEl.ValueKind == JsonValueKind.String && textEl.GetString() is { } text)
                        parts.Add(text);
                }
            }

            var joined = string.Concat(parts).Trim();
            if (!string.IsNullOrEmpty(joined))
                return joined;
        }

        throw new PluginRequestException("Failed to parse OpenAI response text.", PluginRequestFailureKind.EmptyResponse);
    }
}
