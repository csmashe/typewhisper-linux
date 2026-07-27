using System.Text.Json;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

/// <summary>Implements <c>typewhisper models</c>: lists available models as a table or JSON.</summary>
internal static class ModelsCommand
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
                $"{api.BaseUrl}/v1/models",
                requestCts.Token
            );
            var body = await response.Content.ReadAsStringAsync(requestCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ConsoleOutput.Error(
                    $"Models request failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}"
                );
            }

            var validation = ApiResponseValidator.ValidateModels(body);
            if (validation.Error is not null)
            {
                return ApiResponseValidator.ProtocolError(validation.Error);
            }

            if (json)
            {
                Console.WriteLine(JsonFormatting.PrettyJson(body));
                return 0;
            }

            var rows = validation.Value!.Models;
            if (rows.Count == 0)
            {
                Console.WriteLine("No models available.");
                return 0;
            }

            var idWidth = Math.Max(2, rows.Max(m => m.Id.Length));
            var engineWidth = Math.Max(6, rows.Max(m => m.Engine.Length));
            var nameWidth = Math.Max(4, rows.Max(m => m.Name.Length));

            Console.WriteLine(
                $"{ConsoleOutput.Pad("ID", idWidth)}  {ConsoleOutput.Pad("ENGINE", engineWidth)}  {ConsoleOutput.Pad("NAME", nameWidth)}  STATUS"
            );
            Console.WriteLine(new string('-', idWidth + engineWidth + nameWidth + 12));

            foreach (var m in rows)
            {
                var selected = m.Selected ? " *" : "";
                Console.WriteLine(
                    $"{ConsoleOutput.Pad(m.Id, idWidth)}  {ConsoleOutput.Pad(m.Engine, engineWidth)}  {ConsoleOutput.Pad(m.Name, nameWidth)}  {m.Status}{selected}"
                );
            }

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
