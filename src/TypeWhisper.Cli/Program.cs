using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace TypeWhisper.Cli;

/// <summary>
/// TypeWhisper CLI - communicates with the running TypeWhisper app via its REST API.
/// </summary>
static class Program
{
    private const int DefaultPort = 9876;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    // Transcribe can run far longer than the default timeout when --await-download
    // triggers a model fetch on the server side, so use an unbounded HttpClient
    // and bound each request via CancellationTokenSource instead.
    private static readonly HttpClient TranscribeHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"typewhisper-cli {GetVersion()}");
            return 0;
        }

        if (options.Error is not null)
            return Error(options.Error);

        if (options.Command is null)
        {
            PrintUsage();
            return 1;
        }

        // Auto-discovery: pick up port + token from ~/.config/typewhisper/api-discovery.json
        // when neither was explicitly passed. Explicit --port/--token always wins.
        var discovered = TryReadDiscoveryFile();
        var port = options.PortWasExplicit
            ? options.Port
            : discovered?.Port ?? options.Port;
        var token = options.TokenWasExplicit
            ? options.Token
            : options.Token ?? discovered?.Token;

        ApplyAuthorization(token);
        var baseUrl = $"http://127.0.0.1:{port}";

        return options.Command switch
        {
            "status" => await StatusAsync(baseUrl, options.Json),
            "models" => await ModelsAsync(baseUrl, options.Json),
            "transcribe" => await TranscribeAsync(baseUrl, options),
            _ => Error($"Unknown command: {options.Command}"),
        };
    }

    static async Task<int> StatusAsync(string baseUrl, bool json)
    {
        try
        {
            var response = await Http.GetAsync($"{baseUrl}/v1/status");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error(
                    $"Status request failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}"
                );

            if (json)
            {
                Console.WriteLine(PrettyJson(body));
                return 0;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = Prop(root, "status") == "ready" ? "Ready" : "No model loaded";
            var engine = Prop(root, "engine");
            var model = Prop(root, "model");
            Console.WriteLine(
                string.IsNullOrEmpty(model)
                    ? $"{status} - {engine}"
                    : $"{status} - {engine} ({model})"
            );
            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static async Task<int> ModelsAsync(string baseUrl, bool json)
    {
        try
        {
            var response = await Http.GetAsync($"{baseUrl}/v1/models");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Error(
                    $"Models request failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}"
                );

            if (json)
            {
                Console.WriteLine(PrettyJson(body));
                return 0;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models))
                return 0;

            var rows = models.EnumerateArray().ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine("No models available.");
                return 0;
            }

            var idWidth = Math.Max(2, rows.Max(m => Prop(m, "id").Length));
            var engineWidth = Math.Max(6, rows.Max(m => Prop(m, "engine").Length));
            var nameWidth = Math.Max(4, rows.Max(m => Prop(m, "name").Length));

            Console.WriteLine(
                $"{Pad("ID", idWidth)}  {Pad("ENGINE", engineWidth)}  {Pad("NAME", nameWidth)}  STATUS"
            );
            Console.WriteLine(new string('-', idWidth + engineWidth + nameWidth + 10));

            foreach (var m in rows)
            {
                var selected =
                    m.TryGetProperty("selected", out var sel) && sel.GetBoolean() ? " *" : "";
                Console.WriteLine(
                    $"{Pad(Prop(m, "id"), idWidth)}  {Pad(Prop(m, "engine"), engineWidth)}  {Pad(Prop(m, "name"), nameWidth)}  {Prop(m, "status")}{selected}"
                );
            }

            return 0;
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static async Task<int> TranscribeAsync(string baseUrl, CliOptions options)
    {
        if (!string.IsNullOrEmpty(options.Language) && options.LanguageHints.Count > 0)
            return Error("--language and --language-hint cannot be used together.");

        var file = options.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file))
            return Error("Usage: typewhisper transcribe <file|->");

        Stream audioStream;
        var fileName = "audio.wav";

        if (file == "-")
        {
            // Buffer stdin so we can magic-sniff the first bytes and still
            // forward the whole stream to the API. Audio uploads from
            // dictation pipelines fit easily in memory at MaxTranscribeRequestBytes.
            var buffer = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(buffer);
            buffer.Position = 0;
            audioStream = buffer;
            fileName = $"stdin.{StdinAudioSniffer.Detect(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))}";
        }
        else
        {
            if (!File.Exists(file))
                return Error($"File not found: {file}");

            audioStream = File.OpenRead(file);
            fileName = Path.GetFileName(file);
        }

        try
        {
            using (audioStream)
            {
                using var content = new MultipartFormDataContent();
                var fileContent = new StreamContent(audioStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                    "application/octet-stream"
                );
                content.Add(fileContent, "file", fileName);

                AddString(content, "language", options.Language);
                foreach (var hint in options.LanguageHints)
                    AddString(content, "language_hint", hint);
                AddString(content, "task", options.Task);
                AddString(content, "target_language", options.TranslateTo);
                AddString(content, "response_format", options.ResponseFormat);
                AddString(content, "prompt", options.Prompt);
                AddString(content, "engine", options.Engine);
                AddString(content, "model", options.Model);

                var path = options.AwaitDownload
                    ? "/v1/transcribe?await_download=1"
                    : "/v1/transcribe";
                var requestBudget = options.AwaitDownload
                    ? TimeSpan.FromMinutes(15)
                    : TimeSpan.FromMinutes(5);
                using var requestCts = new CancellationTokenSource(requestBudget);

                HttpResponseMessage response;
                string body;
                try
                {
                    response = await TranscribeHttp.PostAsync(
                        $"{baseUrl}{path}",
                        content,
                        requestCts.Token
                    );
                    body = await response.Content.ReadAsStringAsync(requestCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return Error(
                        options.AwaitDownload
                            ? "Transcription timed out while waiting for model download."
                            : "Transcription timed out."
                    );
                }

                if (!response.IsSuccessStatusCode)
                    return Error(
                        $"Transcription failed ({(int)response.StatusCode}): {ExtractErrorMessage(body)}"
                    );

                if (options.Json)
                {
                    Console.WriteLine(PrettyJson(body));
                    return 0;
                }

                using var doc = JsonDocument.Parse(body);
                Console.WriteLine(Prop(doc.RootElement, "text"));
                return 0;
            }
        }
        catch (HttpRequestException)
        {
            return Error("TypeWhisper is not running or API server is disabled.");
        }
    }

    static DiscoveryFile? TryReadDiscoveryFile()
    {
        try
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                );
            }

            var path = Path.Combine(configHome, "typewhisper", "api-discovery.json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int? port = null;
            string? token = null;
            if (root.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number)
                port = portEl.GetInt32();
            if (root.TryGetProperty("token", out var tokenEl) && tokenEl.ValueKind == JsonValueKind.String)
                token = tokenEl.GetString();

            return port is null ? null : new DiscoveryFile(port.Value, token);
        }
        catch
        {
            return null;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine(
            """
            TypeWhisper CLI - Speech-to-Text from the command line

            Usage: typewhisper <command> [options]

            Commands:
              status                    Show TypeWhisper status
              models                    List available models
              transcribe <file|->       Transcribe an audio file, or - for stdin

            Global options:
              --port <N>                API server port (default: 9876, or auto-discovered)
              --token <token>           API bearer token, or TYPEWHISPER_API_TOKEN
              --api-token <token>       Alias of --token (Mac CLI parity)
              --json                    Output as JSON
              --version                 Show version
              --help, -h                Show this help

            Transcribe options:
              --language <code>         Source language (e.g. en, de)
              --language-hint <code>    Repeatable language hint for auto-detection
              --task <task>             transcribe (default) or translate
              --translate-to <code>     Target language for translation
              --response-format <fmt>   json (default) or verbose_json
              --prompt <text>           Prompt/context passed to the engine
              --engine <id>             Override the engine for this request
              --model <id>              Override the model for this request
              --await-download          Wait for local model restore/download

            Examples:
              typewhisper status --token "$TYPEWHISPER_API_TOKEN"
              typewhisper transcribe recording.wav
              typewhisper transcribe recording.wav --language de --json
              typewhisper transcribe recording.wav --language-hint de --language-hint en
              typewhisper transcribe recording.wav --engine groq --model whisper-large-v3-turbo
              typewhisper transcribe - < audio.wav
            """
        );
    }

    static void ApplyAuthorization(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        var auth = new AuthenticationHeaderValue("Bearer", token);
        Http.DefaultRequestHeaders.Authorization = auth;
        TranscribeHttp.DefaultRequestHeaders.Authorization = auth;
    }

    static void AddString(MultipartFormDataContent content, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            content.Add(new StringContent(value), name);
    }

    static string GetVersion()
    {
        var info = Assembly
            .GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
    }

    static string Prop(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true }
            );
        }
        catch
        {
            return json;
        }
    }

    static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (
                    error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                )
                    return message.GetString() ?? body;

                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? body;
            }
        }
        catch { }

        return body;
    }

    static string Pad(string value, int width) => value.PadRight(width);

    static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    private sealed record DiscoveryFile(int Port, string? Token);

    internal sealed record CliOptions
    {
        public string? Command { get; init; }
        public List<string> Positionals { get; init; } = [];
        public int Port { get; init; } = DefaultPort;
        public bool PortWasExplicit { get; init; }
        public string? Token { get; init; }
        public bool TokenWasExplicit { get; init; }
        public bool Json { get; init; }
        public bool ShowHelp { get; init; }
        public bool ShowVersion { get; init; }
        public string? Language { get; init; }
        public List<string> LanguageHints { get; init; } = [];
        public string Task { get; init; } = "transcribe";
        public string? TranslateTo { get; init; }
        public string? ResponseFormat { get; init; }
        public string? Prompt { get; init; }
        public string? Engine { get; init; }
        public string? Model { get; init; }
        public bool AwaitDownload { get; init; }
        public string? Error { get; init; }

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            var positionals = new List<string>();
            var languageHints = new List<string>();
            string? command = null;
            string? language = null;
            string task = "transcribe";
            string? translateTo = null;
            string? responseFormat = null;
            string? prompt = null;
            string? engine = null;
            string? model = null;
            string? token = Environment.GetEnvironmentVariable("TYPEWHISPER_API_TOKEN");
            var tokenWasExplicit = false;
            var port = DefaultPort;
            var portWasExplicit = false;
            var json = false;
            var awaitDownload = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--help":
                    case "-h":
                        return options with { ShowHelp = true };
                    case "--version":
                        return options with { ShowVersion = true };
                    case "--json":
                        json = true;
                        break;
                    case "--await-download":
                        awaitDownload = true;
                        break;
                    case "--port":
                        if (
                            !TryReadValue(args, ref i, out var portValue)
                            || !int.TryParse(portValue, out port)
                            || port < 1
                            || port > 65535
                        )
                            return options with
                            {
                                Error = "--port requires a number between 1 and 65535.",
                            };
                        portWasExplicit = true;
                        break;
                    case "--token":
                    case "--api-token":
                        if (!TryReadValue(args, ref i, out token))
                            return options with { Error = $"{arg} requires a value." };
                        tokenWasExplicit = true;
                        break;
                    case "--language":
                        if (!TryReadValue(args, ref i, out language))
                            return options with { Error = "--language requires a value." };
                        break;
                    case "--language-hint":
                        if (!TryReadValue(args, ref i, out var hint))
                            return options with { Error = "--language-hint requires a value." };
                        languageHints.Add(hint);
                        break;
                    case "--task":
                        if (!TryReadValue(args, ref i, out task))
                            return options with { Error = "--task requires a value." };
                        break;
                    case "--translate-to":
                        if (!TryReadValue(args, ref i, out translateTo))
                            return options with { Error = "--translate-to requires a value." };
                        break;
                    case "--response-format":
                        if (!TryReadValue(args, ref i, out responseFormat))
                            return options with { Error = "--response-format requires a value." };
                        break;
                    case "--prompt":
                        if (!TryReadValue(args, ref i, out prompt))
                            return options with { Error = "--prompt requires a value." };
                        break;
                    case "--engine":
                        if (!TryReadValue(args, ref i, out engine))
                            return options with { Error = "--engine requires a value." };
                        break;
                    case "--model":
                        if (!TryReadValue(args, ref i, out model))
                            return options with { Error = "--model requires a value." };
                        break;
                    default:
                        if (arg.StartsWith('-') && arg != "-")
                            return options with { Error = $"Unknown option '{arg}'." };

                        if (command is null)
                            command = arg;
                        else
                            positionals.Add(arg);
                        break;
                }
            }

            return options with
            {
                Command = command,
                Positionals = positionals,
                Port = port,
                PortWasExplicit = portWasExplicit,
                Token = token,
                TokenWasExplicit = tokenWasExplicit,
                Json = json,
                Language = language,
                LanguageHints = languageHints,
                Task = task,
                TranslateTo = translateTo,
                ResponseFormat = responseFormat,
                Prompt = prompt,
                Engine = engine,
                Model = model,
                AwaitDownload = awaitDownload,
            };
        }

        private static bool TryReadValue(string[] args, ref int index, out string value)
        {
            if (index + 1 >= args.Length)
            {
                value = "";
                return false;
            }

            // Reject candidates that look like option flags (e.g. "--json" after
            // "--port") so a missing value fails fast instead of silently
            // consuming the next switch. A bare "-" is allowed for stdin-style
            // positionals.
            var candidate = args[index + 1];
            if (candidate.Length > 1 && candidate.StartsWith('-'))
            {
                value = "";
                return false;
            }

            value = args[++index];
            return true;
        }
    }
}

/// <summary>
///     Pure-function audio magic-byte sniffer used by the <c>transcribe -</c>
///     stdin path so the server-side filename hint matches the actual
///     container. Returns a short extension (no leading dot). Defaults to
///     "wav" when no header is recognized.
/// </summary>
internal static class StdinAudioSniffer
{
    public static string Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 12
            && head[0] == (byte)'R' && head[1] == (byte)'I'
            && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'A'
            && head[10] == (byte)'V' && head[11] == (byte)'E')
        {
            return "wav";
        }

        if (head.Length >= 4
            && head[0] == (byte)'f' && head[1] == (byte)'L'
            && head[2] == (byte)'a' && head[3] == (byte)'C')
        {
            return "flac";
        }

        if (head.Length >= 4
            && head[0] == (byte)'O' && head[1] == (byte)'g'
            && head[2] == (byte)'g' && head[3] == (byte)'S')
        {
            return "ogg";
        }

        if (head.Length >= 3
            && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
        {
            return "mp3";
        }

        // MPEG audio frame sync (mp3 with no ID3 tag): 0xFF followed by
        // 0xFB / 0xF3 / 0xF2 (or other 0xFx variants for MPEG-2/2.5).
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
        {
            return "mp3";
        }

        return "wav";
    }
}
