// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable CollectionNeverUpdated.Global

namespace TypeWhisper.Cli.Models;

/// <summary>
///     Parsed command-line options for the TypeWhisper CLI. Parsing is pure
///     and side-effect free: <see cref="Parse" /> never touches the network or
///     console, it only validates argument shape and records the first error in
///     <see cref="ErrorMessage" /> so the caller can decide how to report it.
/// </summary>
internal sealed record CliOptions
{
    private const int DefaultPort = 9876;

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
    public string? ErrorMessage { get; init; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        var positionals = new List<string>();
        var languageHints = new List<string>();
        string? command = null;
        string? language = null;
        var task = "transcribe";
        string? translateTo = null;
        string? responseFormat = null;
        string? prompt = null;
        string? engine = null;
        string? model = null;
        var token = Environment.GetEnvironmentVariable("TYPEWHISPER_API_TOKEN");
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
                    {
                        return options with { ErrorMessage = "--port requires a number between 1 and 65535." };
                    }

                    portWasExplicit = true;
                    break;
                case "--token":
                case "--api-token":
                    if (!TryReadValue(args, ref i, out token))
                    {
                        return options with { ErrorMessage = $"{arg} requires a value." };
                    }

                    tokenWasExplicit = true;
                    break;
                case "--language":
                    if (!TryReadValue(args, ref i, out language))
                    {
                        return options with { ErrorMessage = "--language requires a value." };
                    }

                    break;
                case "--language-hint":
                    if (!TryReadValue(args, ref i, out var hint))
                    {
                        return options with { ErrorMessage = "--language-hint requires a value." };
                    }

                    languageHints.Add(hint);
                    break;
                case "--task":
                    if (!TryReadValue(args, ref i, out task))
                    {
                        return options with { ErrorMessage = "--task requires a value." };
                    }

                    break;
                case "--translate-to":
                    if (!TryReadValue(args, ref i, out translateTo))
                    {
                        return options with { ErrorMessage = "--translate-to requires a value." };
                    }

                    break;
                case "--response-format":
                    if (!TryReadValue(args, ref i, out responseFormat))
                    {
                        return options with { ErrorMessage = "--response-format requires a value." };
                    }

                    break;
                case "--prompt":
                    if (!TryReadValue(args, ref i, out prompt))
                    {
                        return options with { ErrorMessage = "--prompt requires a value." };
                    }

                    break;
                case "--engine":
                    if (!TryReadValue(args, ref i, out engine))
                    {
                        return options with { ErrorMessage = "--engine requires a value." };
                    }

                    break;
                case "--model":
                    if (!TryReadValue(args, ref i, out model))
                    {
                        return options with { ErrorMessage = "--model requires a value." };
                    }

                    break;
                default:
                    if (arg.StartsWith('-') && arg != "-")
                    {
                        return options with { ErrorMessage = $"Unknown option '{arg}'." };
                    }

                    if (command is null)
                    {
                        command = arg;
                    }
                    else
                    {
                        positionals.Add(arg);
                    }

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
            AwaitDownload = awaitDownload
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