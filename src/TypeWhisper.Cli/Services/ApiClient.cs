using System.Net.Http.Headers;

namespace TypeWhisper.Cli.Services;

/// <summary>
///     Holds the HTTP clients used to talk to the running TypeWhisper app's
///     REST API and applies bearer auth once at construction. Two clients are
///     kept: a 5-minute one for quick status/models calls, and an unbounded one
///     for transcribe (which can run far longer when <c>--await-download</c>
///     triggers a server-side model fetch; each transcribe request bounds
///     itself with a <see cref="CancellationTokenSource" /> instead).
/// </summary>
internal sealed class ApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient _transcribeHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public ApiClient(string baseUrl, string? token)
    {
        BaseUrl = baseUrl;

        if (string.IsNullOrWhiteSpace(token))
            return;

        var auth = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Authorization = auth;
        _transcribeHttp.DefaultRequestHeaders.Authorization = auth;
    }

    public string BaseUrl { get; }
    public HttpClient Http => _http;
    public HttpClient TranscribeHttp => _transcribeHttp;
}
