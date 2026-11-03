using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

internal static class HistoryCommand
{
    public static Task<int> RunAsync(
        ApiClient api, CliOptions options, CancellationToken ct, TimeSpan? budget = null
    )
    {
        var limit = options.IsLast ? 1 : options.Limit;
        var offset = options.IsLast ? 0 : options.Offset;
        var path = FormattableString.Invariant($"{api.BaseUrl}/v1/history?limit={limit}&offset={offset}");
        if (options is { IsLast: false, Query: not null })
        {
            path += $"&q={Uri.EscapeDataString(options.Query)}";
        }

        return QuickApiCommand.RunAsync(api, new HttpRequestMessage(HttpMethod.Get, path), budget, body =>
        {
            var validation = ApiResponseValidator.ValidateHistory(body);
            if (validation.Error is not null)
            {
                return ApiResponseValidator.ProtocolError(validation.Error);
            }

            if (options.Json)
            {
                Console.WriteLine(JsonFormatting.PrettyJson(body));
                return ExitCodes.Success;
            }

            var records = validation.Value!.Root.GetProperty("records");
            if (records.GetArrayLength() == 0)
            {
                Console.WriteLine("No history entries.");
            }
            else if (options.IsLast)
            {
                Console.WriteLine(ApiResponseValidator.OptionalString(records[0], "text"));
            }
            else
            {
                foreach (var record in records.EnumerateArray())
                {
                    Console.WriteLine($"{ApiResponseValidator.OptionalString(record, "timestamp")}  {ApiResponseValidator.OptionalString(record, "text")}");
                }
            }

            return ExitCodes.Success;
        }, ct);
    }
}