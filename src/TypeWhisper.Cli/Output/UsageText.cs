namespace TypeWhisper.Cli.Output;

/// <summary>The CLI usage/help text printed for <c>--help</c> and bad invocations.</summary>
internal static class UsageText
{
    public static void Print()
    {
        Console.WriteLine(
            """
            TypeWhisper CLI - Speech-to-Text from the command line

            Usage: typewhisper-cli <command> [options]

            Commands:
              status                    Show TypeWhisper status
              models                    List available models
              transcribe <file|->       Transcribe an audio file, or - for stdin

            Global options:
              --token <token>           API bearer token, or TYPEWHISPER_API_TOKEN
              --api-token <token>       Alias of --token (Mac CLI parity)
              --json                    Output as JSON
              --version                 Show version
              --help, -h                Show this help
              --                        Treat remaining arguments as file operands

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
              typewhisper-cli status --token "$TYPEWHISPER_API_TOKEN"
              typewhisper-cli transcribe recording.wav
              typewhisper-cli transcribe recording.wav --language de --json
              typewhisper-cli transcribe recording.wav --language-hint de --language-hint en
              typewhisper-cli transcribe recording.wav --engine groq --model whisper-large-v3-turbo
              typewhisper-cli transcribe - < audio.wav
            """
        );
    }
}
