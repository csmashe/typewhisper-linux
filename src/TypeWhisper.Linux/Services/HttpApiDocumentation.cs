namespace TypeWhisper.Linux.Services;

internal static class HttpApiDocumentation
{
    public static string Html(int port) => Template
        .Replace("{{PORT}}", port.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{{ENDPOINTS}}", string.Join("\n", HttpApiRoutes.Descriptions.Select(route =>
            $"<tr><td>{route.Method}</td><td><code>{route.Path}</code></td><td>{System.Net.WebUtility.HtmlEncode(route.Description)}</td></tr>")));

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>TypeWhisper HTTP API</title>
        <style>
        :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
        body { max-width: 65rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
        table { border-collapse: collapse; width: 100%; } th, td { text-align: left; padding: .4rem; border: 1px solid #888; }
        pre { overflow-x: auto; padding: 1rem; border: 1px solid #888; } code { overflow-wrap: anywhere; }
        </style>
        </head>
        <body>
        <h1>TypeWhisper HTTP API</h1>
        <p>Base address: <code>http://127.0.0.1:{{PORT}}</code>. Requests must use a loopback host and an allowed origin.</p>
        <h2>Authentication and quick start</h2>
        <p>Find your token in Settings → Advanced → HTTP API. Send <code>Authorization: Bearer &lt;token&gt;</code>.
        Header names are case-insensitive, so <code>authorization: Bearer &lt;token&gt;</code> also works; the Bearer prefix is case-sensitive.
        Only GET /docs and GET /docs/ are public; API routes including /v1/status require authentication.</p>
        <p>Set the TOKEN environment variable to your token, then run:</p>
        <pre><code>curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:{{PORT}}/v1/status
        curl -H "Authorization: Bearer $TOKEN" -F file=@clip.wav \
          -F response_format=json http://127.0.0.1:{{PORT}}/v1/transcribe</code></pre>
        <h2>CLI</h2>
        <pre><code>typewhisper status
        typewhisper models
        typewhisper transcribe &lt;file&gt;
        typewhisper --help</code></pre>
        <h2>Endpoints</h2>
        <table><thead><tr><th>Method</th><th>Path</th><th>Description</th></tr></thead><tbody>
        {{ENDPOINTS}}
        </tbody></table>
        <p>OPTIONS is available for CORS preflight. A known path with an unsupported method returns 405 with an Allow header.</p>
        <h2>Transcription options</h2>
        <p>Upload audio as multipart field <code>file</code>, send raw audio, or POST JSON with a <code>path</code> to /v1/transcribe/local-file.
        Multipart uses the option names below; raw audio uses corresponding x- headers with hyphens (for example x-response-format).
        Local-file JSON uses snake_case keys.</p>
        <table><thead><tr><th>Option</th><th>Meaning</th></tr></thead><tbody>
        <tr><td>language</td><td>Explicit language; cannot be combined with language hints.</td></tr>
        <tr><td>language_hint(s)</td><td>Repeat language_hint in multipart; use comma-separated x-language-hints for raw audio or a language_hints JSON array.</td></tr>
        <tr><td>task</td><td>transcribe (default) or translate.</td></tr>
        <tr><td>target_language</td><td>Translate the resulting text and, when requested, segments to this language.</td></tr>
        <tr><td>response_format</td><td>json (default), verbose_json, text, srt, or vtt.</td></tr>
        <tr><td>prompt</td><td>Optional context for the transcription engine.</td></tr>
        <tr><td>engine</td><td>Request-scoped engine override.</td></tr>
        <tr><td>model</td><td>Request-scoped model override; use engine to disambiguate bare model IDs.</td></tr>
        <tr><td>await_download</td><td>Query option for uploads/raw audio, JSON boolean for local files; default false.</td></tr>
        <tr><td>apply_corrections</td><td>Apply dictionary corrections (default true). Multipart field, x-apply-corrections raw header, or query option; JSON boolean for local files.</td></tr>
        </tbody></table>
        <p>Boolean strings accept true/false, 1/0, yes/no, or on/off, ignoring case and surrounding whitespace.
        JSON requires real booleans. Conflicting apply_corrections values from the query and body/header return 400.</p>
        <h2>Response formats</h2>
        <p>json returns text, language, duration, no_speech_probability, engine, and model.
        verbose_json adds segments with text, start, end, and no_speech_probability.</p>
        <p>text returns the final transcript as text/plain. srt returns application/x-subrip; vtt returns text/vtt beginning with WEBVTT.
        All text responses use UTF-8. Subtitles require at least one provider segment with finite timestamps, nonnegative starts,
        an end after its start when rounded to milliseconds, and non-decreasing starts; otherwise the API returns 422.
        Segment translation preserves provider timing.</p>
        <h2>Discovery</h2>
        <p>ApiDiscoveryFile publishes <code>$XDG_CONFIG_HOME/typewhisper/api-discovery.json</code>, defaulting to
        <code>~/.config/typewhisper/api-discovery.json</code>. It contains version, port, socket_path, and token;
        it is restricted to the current user and removed when the API stops.</p>
        <h2>Status codes</h2>
        <table><thead><tr><th>Code</th><th>Meaning</th></tr></thead><tbody>
        <tr><td>400</td><td>Invalid request, options, or JSON.</td></tr>
        <tr><td>401</td><td>Missing or invalid bearer token.</td></tr>
        <tr><td>403</td><td>Forbidden origin or host.</td></tr>
        <tr><td>404</td><td>Unknown route or requested resource.</td></tr>
        <tr><td>405</td><td>Unsupported method for a known path; see Allow.</td></tr>
        <tr><td>409</td><td>Conflicting profile shortcut or dictation state.</td></tr>
        <tr><td>413</td><td>Request body exceeds the upload or JSON limit.</td></tr>
        <tr><td>422</td><td>Subtitle output requires valid segment timestamps.</td></tr>
        <tr><td>429</td><td>Request queue is full.</td></tr>
        <tr><td>500</td><td>Unexpected server error.</td></tr>
        <tr><td>501</td><td>Requested translation is unavailable or unsupported.</td></tr>
        <tr><td>502</td><td>Translation did not preserve segment count.</td></tr>
        <tr><td>503</td><td>Service, model, download, or audio conversion is unavailable.</td></tr>
        </tbody></table>
        </body></html>
        """;
}
