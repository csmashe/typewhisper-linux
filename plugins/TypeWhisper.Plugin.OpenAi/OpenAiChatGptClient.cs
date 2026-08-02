// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed class OpenAiChatGptClient
{
    internal const string Endpoint = "https://chatgpt.com/backend-api/codex/responses";

    private readonly HttpClient _httpClient;
    private readonly string _accessToken;
    private readonly string? _accountId;

    public OpenAiChatGptClient(HttpClient httpClient, string accessToken, string? accountId)
    {
        _httpClient = httpClient;
        _accessToken = accessToken;
        _accountId = accountId;
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        string? reasoningEffort,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(_accountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", _accountId);

        request.Content = OpenAiJson.CreateJsonContent(
            CreateRequestBody(model, systemPrompt, userText, reasoningEffort));

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseErrorMessage(body, (int)response.StatusCode));

        return await ParseResponseTextAsync(body, ct)
            ?? throw new InvalidOperationException("The ChatGPT response could not be parsed.");
    }

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string model,
        string systemPrompt,
        string userText,
        string? reasoningEffort)
    {
        var instructions = string.IsNullOrWhiteSpace(systemPrompt)
            ? "You are a helpful assistant."
            : systemPrompt;

        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = OpenAiJson.Element(model),
            ["instructions"] = OpenAiJson.Element(instructions),
            ["input"] = OpenAiJson.Element(new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_text", text = userText },
                    },
                },
            }),
            ["store"] = OpenAiJson.Element(false),
            ["stream"] = OpenAiJson.Element(true),
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning"] = OpenAiJson.Element(new { effort = reasoningEffort });

        return body;
    }

    internal static async Task<string?> ParseResponseTextAsync(
        string body,
        CancellationToken cancellationToken = default)
    {
        if (TryParseJsonResponseText(body, out var responseText))
            return responseText;

        using var reader = new StringReader(body);
        return await ParseEventStreamResponseTextAsync(reader, cancellationToken);
    }

    private static async Task<string?> ParseEventStreamResponseTextAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var deltaBuffer = new StringBuilder();
        var completedParts = new List<string>();

        await foreach (var part in SseEventDecoder.ReadValidatedAsync(
                           reader,
                           ChatGptSsePolicy.Instance,
                           cancellationToken))
        {
            if (part.IsIncremental)
                deltaBuffer.Append(part.Text);
            else
                completedParts.Add(part.Text);
        }

        if (deltaBuffer.Length > 0)
            return deltaBuffer.ToString().Trim();

        var completed = string.Join("\n", completedParts).Trim();
        return string.IsNullOrEmpty(completed) ? null : completed;
    }

    private readonly record struct ChatGptTextPart(string Text, bool IsIncremental);

    private sealed class ChatGptSsePolicy : ISseEventPolicy<ChatGptTextPart>
    {
        public static ChatGptSsePolicy Instance { get; } = new();

        public string StreamName => "ChatGPT SSE stream";
        public string ExpectedTerminal => "[DONE]";

        public SsePolicyDecision<ChatGptTextPart> Evaluate(SseEvent sseEvent)
        {
            if (sseEvent.Data == "[DONE]")
            {
                return new SsePolicyDecision<ChatGptTextPart>(
                    AcceptTerminal: true,
                    EndStream: true);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(sseEvent.Data);
            }
            catch (JsonException)
            {
                return default;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (GetString(root, "type") is not { } type)
                    return default;

                if (GetSseFailure(root, type) is { } failure)
                {
                    return new SsePolicyDecision<ChatGptTextPart>(
                        Error: new InvalidOperationException(failure));
                }

                switch (type)
                {
                    case "response.output_text.delta":
                        if (GetString(root, "delta") is { } delta)
                        {
                            return new SsePolicyDecision<ChatGptTextPart>(
                                HasDelta: true,
                                Delta: new ChatGptTextPart(delta, true));
                        }

                        break;
                    case "response.output_text.done":
                        if (GetString(root, "text") is { Length: > 0 } text)
                        {
                            return new SsePolicyDecision<ChatGptTextPart>(
                                HasDelta: true,
                                Delta: new ChatGptTextPart(text, false));
                        }

                        break;
                    case "response.content_part.done":
                        if (root.TryGetProperty("part", out var part)
                            && GetString(part, "text") is { Length: > 0 } partText)
                        {
                            return new SsePolicyDecision<ChatGptTextPart>(
                                HasDelta: true,
                                Delta: new ChatGptTextPart(partText, false));
                        }

                        break;
                }
            }

            return default;
        }
    }

    private static string? GetSseFailure(JsonElement root, string type)
    {
        var status = root.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            ? GetString(response, "status")
            : GetString(root, "status");

        if (type == "response.completed")
        {
            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return $"ChatGPT SSE event 'response.completed' had non-completed status "
                    + $"'{status ?? "missing"}'.";
            }

            return null;
        }

        if (type is not ("error"
            or "response.failed"
            or "response.incomplete"
            or "response.cancelled"
            or "response.canceled"))
        {
            return null;
        }

        return status is null
            ? $"ChatGPT SSE event '{type}' indicated failure."
            : $"ChatGPT SSE event '{type}' indicated terminal status '{status}'.";
    }

    private static bool TryParseJsonResponseText(string json, out string? responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (GetString(root, "output_text") is { Length: > 0 } outputText)
            {
                responseText = outputText.Trim();
                return true;
            }

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && GetString(message, "content") is { Length: > 0 } messageContent)
            {
                responseText = messageContent.Trim();
                return true;
            }

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

                    parts.AddRange(content.EnumerateArray()
                        .Select(block => GetString(block, "text"))
                        .Where(text => !string.IsNullOrWhiteSpace(text))!);
                }

                var joined = string.Join("\n", parts).Trim();
                if (!string.IsNullOrEmpty(joined))
                {
                    responseText = joined;
                    return true;
                }
            }

            responseText = null;
            return true;
        }
        catch (JsonException)
        {
            responseText = null;
            return false;
        }
    }

    private static string ParseErrorMessage(string body, int statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (GetString(root, "detail") is { Length: > 0 } detail)
                return detail;
            if (GetString(root, "message") is { Length: > 0 } message)
                return message;
            if (root.TryGetProperty("error", out var error))
            {
                if (GetString(error, "message") is { Length: > 0 } apiMessage)
                    return apiMessage;
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? $"HTTP {statusCode}";
            }
        }
        catch (JsonException)
        {
            return $"HTTP {statusCode}";
        }

        return $"HTTP {statusCode}";
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
