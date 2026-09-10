using System.Net.Sockets;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>Shared HTTP failure classification for provider requests.</summary>
public static class OpenAiApiHelper
{
    /// <summary>Sends a request and buffers its response body, classifying HTTP and transport failures.</summary>
    /// <param name="httpClient">The caller-owned HTTP client.</param>
    /// <param name="request">The caller-owned request.</param>
    /// <param name="ct">The caller's cancellation token. Caller cancellation propagates as cancellation;
    /// cancellation from another source is classified as <see cref="PluginRequestFailureKind.Timeout"/>.</param>
    /// <returns>A successful response that the caller must dispose. Failed responses are disposed here.</returns>
    /// <exception cref="PluginRequestException">An HTTP, network, or timeout failure occurred.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    public static Task<HttpResponseMessage> SendWithErrorHandlingAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken ct
    ) => SendWithErrorHandlingAsync(httpClient, request, HttpCompletionOption.ResponseContentRead, ct);

    /// <summary>Sends a request with configurable response completion and error formatting,
    /// classifying HTTP and transport failures and sanitizing failure messages.</summary>
    /// <param name="httpClient">The caller-owned HTTP client.</param>
    /// <param name="request">The caller-owned request.</param>
    /// <param name="ct">The caller's cancellation token. Caller cancellation propagates as cancellation;
    /// cancellation from another source is classified as <see cref="PluginRequestFailureKind.Timeout"/>.</param>
    /// <param name="completionOption">Whether to buffer the body or return after response headers arrive.</param>
    /// <param name="errorMessage">Optional formatter receiving the failed response and its credential-redacted
    /// body. The returned message is redacted and summarized before being included in the exception.</param>
    /// <returns>A successful response that the caller must dispose. Failed responses are disposed here.
    /// When returning after headers, subsequent body reads can use <see cref="ReadBodyWithErrorHandlingAsync{T}"/>.</returns>
    /// <exception cref="PluginRequestException">An HTTP, network, or timeout failure occurred.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    public static async Task<HttpResponseMessage> SendWithErrorHandlingAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken ct,
        Func<HttpResponseMessage, string, string>? errorMessage = null
    )
    {
        try
        {
            var response = await httpClient.SendAsync(request, completionOption, ct);
            if (response.IsSuccessStatusCode)
                return response;

            using (response)
            {
                var body = RedactMessage(await response.Content.ReadAsStringAsync(ct), httpClient, request);
                var status = (int)response.StatusCode;
                var message = errorMessage?.Invoke(response, body) ?? status switch
                {
                    401 => "Invalid API key",
                    413 => "Audio too large (max 25 MB)",
                    429 => "Rate limit reached, please wait",
                    _ => $"API error {status}: {ExtractErrorMessage(body)}",
                };
                var retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAt)
                    retryAfter = retryAt - DateTimeOffset.UtcNow;
                if (retryAfter < TimeSpan.Zero)
                    retryAfter = TimeSpan.Zero;

                throw new PluginRequestException(
                    Summarize(RedactMessage(message, httpClient, request)),
                    status switch
                    {
                        401 => PluginRequestFailureKind.Authentication,
                        403 => PluginRequestFailureKind.Permission,
                        408 => PluginRequestFailureKind.Timeout,
                        413 => PluginRequestFailureKind.RequestTooLarge,
                        429 => PluginRequestFailureKind.RateLimit,
                        >= 500 and <= 599 => PluginRequestFailureKind.ServerError,
                        >= 400 and <= 499 => PluginRequestFailureKind.InvalidRequest,
                        _ => PluginRequestFailureKind.Unknown,
                    },
                    status,
                    retryAfter
                );
            }
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException(
                Summarize(RedactMessage($"Network error: {ex.Message}", httpClient, request)),
                PluginRequestFailureKind.Network,
                innerException: ex
            );
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                "API request timed out.", PluginRequestFailureKind.Timeout, innerException: ex
            );
        }
    }

    /// <summary>Classifies transport failures while opening or reading a response body.</summary>
    /// <typeparam name="T">The result of the body operation.</typeparam>
    /// <param name="read">The body operation, which must pass the caller's token to cancellable reads.</param>
    /// <param name="ct">The caller's cancellation token. Caller cancellation propagates as cancellation;
    /// cancellation from another source is classified as <see cref="PluginRequestFailureKind.Timeout"/>.</param>
    /// <returns>The operation's result. Ownership of the response, stream, and result stays with the caller.</returns>
    /// <exception cref="PluginRequestException">A network or timeout failure occurred.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    public static async Task<T> ReadBodyWithErrorHandlingAsync<T>(
        Func<Task<T>> read, CancellationToken ct)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or SocketException)
        {
            throw MapBodyReadFailure(ex, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw MapBodyReadFailure(ex, ct);
        }
    }

    /// <summary>
    ///     Classifies an exception thrown while reading a response body, so readers that cannot
    ///     route through <see cref="ReadBodyWithErrorHandlingAsync{T}" /> (streaming decoders that
    ///     must not allocate a closure per line) classify transport failures identically.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    public static PluginRequestException MapBodyReadFailure(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException)
        {
            return new PluginRequestException(
                "API response body timed out.", PluginRequestFailureKind.Timeout, innerException: ex);
        }

        ct.ThrowIfCancellationRequested();
        return new PluginRequestException(
            "Network error while reading the response body.",
            PluginRequestFailureKind.Network, innerException: ex);
    }

    private static string RedactMessage(string message, HttpClient client, HttpRequestMessage request)
    {
        // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; the Aggregate form reads worse for a rolling string rewrite.
        foreach (var header in request.Headers.Concat(client.DefaultRequestHeaders))
        {
            if (!header.Key.Contains("key", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Contains("token", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; the Aggregate form reads worse for a rolling string rewrite.
            foreach (var value in header.Value)
            {
                var secret = value.Contains(' ') ? value[(value.IndexOf(' ') + 1)..] : value;
                if (!string.IsNullOrEmpty(secret))
                    message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        // ReSharper disable once InvertIf -- inverting would duplicate the return below; kept nested for clarity.
        if (request.RequestUri is { } uri)
        {
            // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; the Aggregate form reads worse for a rolling string rewrite.
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2
                    || (!pair[0].Contains("key", StringComparison.OrdinalIgnoreCase)
                        && !pair[0].Contains("token", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var secret = Uri.UnescapeDataString(pair[1]);
                if (secret.Length > 0)
                    message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return message;
    }

    /// <summary>Extracts a string from an OpenAI-style JSON error or falls back to the raw body.</summary>
    /// <param name="errorBody">The error response body.</param>
    /// <returns>The extracted message with whitespace collapsed and trimmed, capped at 300 characters
    /// including a trailing ellipsis when truncated. This method does not redact credentials.</returns>
    private static string ExtractErrorMessage(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                    return Summarize(message.GetString() ?? errorBody);
                if (error.ValueKind == JsonValueKind.String)
                    return Summarize(error.GetString() ?? errorBody);
            }
        }
        catch (JsonException)
        {
        }

        return Summarize(errorBody);
    }

    private static string Summarize(string message)
    {
        message = string.Join(' ', message.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return message.Length > 300 ? message[..299] + "…" : message;
    }
}
