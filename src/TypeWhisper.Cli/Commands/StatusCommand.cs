using System.Text.Json;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

/// <summary>Implements <c>typewhisper status</c>: reports engine/model readiness.</summary>
internal static class StatusCommand
{
    private static readonly TimeSpan s_defaultBudget = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(
        ApiClient api,
        bool json,
        CancellationToken ct,
        TimeSpan? budget = null
    )
    {
        var requestBudget = budget ?? s_defaultBudget;
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(requestBudget);

        try
        {
            using var response = await api.Http.GetAsync(
                $"{api.BaseUrl}/v1/status",
                requestCts.Token
            );
            var body = await response.Content.ReadAsStringAsync(requestCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ConsoleOutput.Error(
                    $"Status request failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}"
                );
            }

            var validation = ApiResponseValidator.ValidateStatus(body);
            if (validation.Error is not null)
            {
                return ApiResponseValidator.ProtocolError(validation.Error);
            }

            if (json)
            {
                Console.WriteLine(JsonFormatting.PrettyJson(body));
                return 0;
            }

            var result = validation.Value!;
            var status = result.Status == "ready" ? "Ready" : "No model loaded";
            Console.WriteLine(
                string.IsNullOrEmpty(result.Model)
                    ? $"{status} - {result.Engine}"
                    : $"{status} - {result.Engine} ({result.Model})"
            );
            return 0;
        }
        catch (HttpRequestException)
        {
            return ConsoleOutput.Error("TypeWhisper is not running or API server is disabled.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ConsoleOutput.Error("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            return ConsoleOutput.Error(
                $"The API did not respond within {FormatBudget(requestBudget)}."
            );
        }
        catch (JsonException)
        {
            return ConsoleOutput.Error("Received malformed JSON from the API.");
        }
    }

    private static string FormatBudget(TimeSpan budget)
    {
        var seconds = budget.TotalSeconds.ToString(
            "0.###",
            System.Globalization.CultureInfo.InvariantCulture
        );
        return seconds == "1" ? "1 second" : $"{seconds} seconds";
    }
}
