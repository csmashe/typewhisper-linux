// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
///     Shared HTTP error handling for OpenAI-compatible API calls.
/// </summary>
// ReSharper disable once UnusedType.Global
public static class OpenAiApiHelper
{
    /// <summary>
    ///     Sends an HTTP request and throws <see cref="InvalidOperationException" /> for
    ///     network failures, timeouts, and non-success HTTP status codes, converting the
    ///     raw error body into a human-readable message where possible.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static async Task<HttpResponseMessage> SendWithErrorHandlingAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Network error: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("API request timed out.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var message = (int)response.StatusCode switch
            {
                401 => "Invalid API key",
                413 => "Audio too large (max 25 MB)",
                429 => "Rate limit reached, please wait",
                _ => $"API error {(int)response.StatusCode}: {ExtractErrorMessage(errorBody)}",
            };
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    ///     Extracts a human-readable error message from an OpenAI-style error JSON body.
    ///     Falls back to truncating the raw body if parsing fails.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static string ExtractErrorMessage(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                if (
                    errorEl.ValueKind == JsonValueKind.Object
                    && errorEl.TryGetProperty("message", out var msgEl)
                )
                {
                    return msgEl.GetString() ?? errorBody;
                }

                if (errorEl.ValueKind == JsonValueKind.String)
                {
                    return errorEl.GetString() ?? errorBody;
                }
            }
        }
        catch
        {
            // JSON parsing failed, fall through to truncation
        }

        return errorBody.Length > 200 ? errorBody[..200] : errorBody;
    }
}
