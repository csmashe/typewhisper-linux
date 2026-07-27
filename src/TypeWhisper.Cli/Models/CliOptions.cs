// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable CollectionNeverUpdated.Global

namespace TypeWhisper.Cli.Models;

/// <summary>
///     Parsed command-line options. <see cref="Parse" /> is pure and side-effect free;
///     it records the first parse error in <see cref="ErrorMessage" /> for the caller
///     to handle rather than writing to the console itself.
/// </summary>
internal sealed record CliOptions
{
    public string? Command { get; init; }
    public List<string> Positionals { get; init; } = [];
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
        var transcribeOptions = new List<string>();
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
        var json = false;
        var awaitDownload = false;
        var parseOptions = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- these are guard clauses on different subjects (the "--" separator vs. the post-separator operand mode); a switch on parseOptions would sit right above the switch on arg below and read worse.
            if (parseOptions && arg == "--")
            {
                parseOptions = false;
                continue;
            }

            if (!parseOptions)
            {
                if (command is null)
                {
                    command = arg;
                }
                else
                {
                    positionals.Add(arg);
                }

                continue;
            }

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
                    transcribeOptions.Add(arg);
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

                    transcribeOptions.Add(arg);
                    break;
                case "--language-hint":
                    if (!TryReadValue(args, ref i, out var hint))
                    {
                        return options with { ErrorMessage = "--language-hint requires a value." };
                    }

                    languageHints.Add(hint);
                    transcribeOptions.Add(arg);
                    break;
                case "--task":
                    if (!TryReadValue(args, ref i, out var taskValue))
                    {
                        return options with { ErrorMessage = "--task requires a value." };
                    }

                    var normalizedTask = NormalizeTask(taskValue);
                    if (normalizedTask is null)
                    {
                        return options with
                        {
                            ErrorMessage =
                                $"Invalid value '{taskValue}' for --task. Allowed values: transcribe, translate.",
                        };
                    }

                    task = normalizedTask;
                    transcribeOptions.Add(arg);
                    break;
                case "--translate-to":
                    if (!TryReadValue(args, ref i, out translateTo))
                    {
                        return options with { ErrorMessage = "--translate-to requires a value." };
                    }

                    transcribeOptions.Add(arg);
                    break;
                case "--response-format":
                    if (!TryReadValue(args, ref i, out var responseFormatValue))
                    {
                        return options with { ErrorMessage = "--response-format requires a value." };
                    }

                    var normalizedResponseFormat = NormalizeResponseFormat(responseFormatValue);
                    if (normalizedResponseFormat is null)
                    {
                        return options with
                        {
                            ErrorMessage =
                                $"Invalid value '{responseFormatValue}' for --response-format. Allowed values: json, verbose_json.",
                        };
                    }

                    responseFormat = normalizedResponseFormat;
                    transcribeOptions.Add(arg);
                    break;
                case "--prompt":
                    if (!TryReadValue(args, ref i, out prompt))
                    {
                        return options with { ErrorMessage = "--prompt requires a value." };
                    }

                    transcribeOptions.Add(arg);
                    break;
                case "--engine":
                    if (!TryReadValue(args, ref i, out engine))
                    {
                        return options with { ErrorMessage = "--engine requires a value." };
                    }

                    transcribeOptions.Add(arg);
                    break;
                case "--model":
                    if (!TryReadValue(args, ref i, out model))
                    {
                        return options with { ErrorMessage = "--model requires a value." };
                    }

                    transcribeOptions.Add(arg);
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

        var parsed = options with
        {
            Command = command,
            Positionals = positionals,
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

        var grammarError = command switch
        {
            "status" or "models" when transcribeOptions.Count > 0 =>
                $"Option '{transcribeOptions[0]}' is not valid for '{command}'.",
            "status" or "models" when positionals.Count > 0 =>
                $"Unexpected operand '{positionals[0]}' for '{command}'.",
            "transcribe" when positionals.Count == 0 =>
                "Command 'transcribe' requires exactly one file operand.",
            "transcribe" when positionals.Count > 1 =>
                $"Unexpected operand '{positionals[1]}' for 'transcribe'.",
            _ => null,
        };

        return grammarError is null ? parsed : parsed with { ErrorMessage = grammarError };
    }

    private static string? NormalizeTask(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "transcribe" or "translate" ? normalized : null;
    }

    private static string? NormalizeResponseFormat(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "json" or "verbose_json" ? normalized : null;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = "";
            return false;
        }

        // Reject flag-looking tokens (e.g. "--json" after "--token") so a missing
        // value fails fast. A bare "-" is allowed for stdin-style positionals.
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
