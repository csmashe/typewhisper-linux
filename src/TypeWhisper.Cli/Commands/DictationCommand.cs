using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

internal static class DictationCommand
{
    // Stop returns only after the recording is transcribed and inserted.
    private static readonly TimeSpan s_stopBudget = TimeSpan.FromMinutes(5);

    public static Task<int> RunAsync(
        ApiClient api, CliOptions options, CancellationToken ct, TimeSpan? budget = null
    )
    {
        var action = options.Action;
        var path = action == "result"
            ? $"transcription?sessionId={Uri.EscapeDataString(options.Positionals[1])}"
            : action;
        var method = action is "start" or "stop" ? HttpMethod.Post : HttpMethod.Get;
        var request = new HttpRequestMessage(method, $"{api.BaseUrl}/v1/dictation/{path}");
        var requestBudget = budget ?? (action == "stop" ? s_stopBudget : null);
        return QuickApiCommand.RunAsync(api, request, requestBudget, body =>
        {
            var validation = action switch
            {
                "start" => ApiResponseValidator.ValidateDictationStart(body),
                "stop" => ApiResponseValidator.ValidateDictationStop(body),
                "status" => ApiResponseValidator.ValidateDictationStatus(body),
                _ => ApiResponseValidator.ValidateDictationResult(body),
            };
            if (validation.Error is not null)
            {
                return ApiResponseValidator.ProtocolError(validation.Error);
            }

            var root = validation.Value!.Root;
            var state = ApiResponseValidator.OptionalString(root, "state");
            if (action == "result" && state == "failed")
            {
                var error = ApiResponseValidator.OptionalString(root, "error");
                if (string.IsNullOrEmpty(error))
                    error = ApiResponseValidator.OptionalString(root, "message");
                return ConsoleOutput.Error(string.IsNullOrEmpty(error) ? "Dictation failed." : error, ExitCodes.ServerError);
            }

            // Canceled and discarded sessions carry an empty text plus a message.
            var message = ApiResponseValidator.OptionalString(root, "message");
            Console.WriteLine(options.Json ? JsonFormatting.PrettyJson(body) : action switch
            {
                "start" => $"started: {root.GetProperty("session_id").GetInt64()}",
                "stop" => "stopped",
                "result" when ApiResponseValidator.OptionalString(root, "text") is { Length: > 0 } text => text,
                _ => message.Length > 0 ? $"{state}: {message}" : state,
            });
            return ExitCodes.Success;
        }, ct);
    }
}