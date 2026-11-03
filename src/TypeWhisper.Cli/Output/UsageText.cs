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
              models [list]             List available models
              models load               Load --engine <id> [--model <id>]
              models unload             Unload [--engine <id>]
              models delete             Delete --engine <id> --model <id>
              history [search <query>]  List or search transcription history
              history last / last       Print the latest transcription text
              dictation start|stop      Start or stop dictation
              dictation status          Show dictation state
              dictation result <id>     Get a session's transcription
              transcribe [file|-]       Transcribe a file or piped stdin

            Global options:
              --token <token>           API bearer token, or TYPEWHISPER_API_TOKEN
              --api-token <token>       Alias of --token (Mac CLI parity)
              --json                    Output as JSON
              --version                 Show version
              --help, -h                Show this help
              --                        Treat remaining arguments as file operands

            History options:
              --query <text>            Search text (alternative to search <query>)
              --limit <n>               Maximum entries, 0–200 (default 50)
              --offset <n>              Skip entries, non-negative (default 0)

            Transcribe options:
              --language <code>         Source language (e.g. en, de)
              --language-hint <code>    Repeatable language hint for auto-detection
              --task <task>             transcribe (default) or translate
              --translate-to <code>     Target language for translation
              --response-format <fmt>   json (default), verbose_json, text, srt, vtt
              --prompt <text>           Prompt/context passed to the engine
              --engine <id>             Override the engine for this request
              --model <id>              Override the model for this request
              --no-corrections          Skip dictionary corrections
              --await-download          Wait for local model restore/download

            Exit codes:
              0                         Success
              1                         Usage, local input error, or cancellation
              2                         App unavailable or request timed out
              3                         Server error or malformed response

            Examples:
              typewhisper-cli status --token "$TYPEWHISPER_API_TOKEN"
              typewhisper-cli transcribe recording.wav
              typewhisper-cli transcribe recording.wav --language de --json
              typewhisper-cli transcribe recording.wav --language-hint de --language-hint en
              typewhisper-cli transcribe recording.wav --engine groq --model whisper-large-v3-turbo
              typewhisper-cli transcribe - < audio.wav
              typewhisper-cli transcribe < audio.wav
              typewhisper-cli transcribe recording.wav --no-corrections --response-format srt
              typewhisper-cli last
              typewhisper-cli history --limit 20
              typewhisper-cli history search hello
              typewhisper-cli dictation start
              typewhisper-cli dictation result 7
              typewhisper-cli models load --engine whisper --model tiny
            """
        );
    }
}
