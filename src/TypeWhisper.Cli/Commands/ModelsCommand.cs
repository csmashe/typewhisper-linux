using System.Text;
using System.Text.Json;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

/// <summary>Implements <c>typewhisper models</c>: lists available models as a table or JSON.</summary>
internal static class ModelsCommand
{
    private static readonly TimeSpan s_defaultBudget = TimeSpan.FromSeconds(10);

    // Loading may first provision the CUDA runtime or download the model.
    private static readonly TimeSpan s_loadBudget = TimeSpan.FromMinutes(15);

    public static Task<int> RunAsync(
        ApiClient api, CliOptions options, CancellationToken ct, TimeSpan? budget = null
    )
    {
        if (options.Action is null or "list")
        {
            return RunAsync(api, options.Json, ct, budget);
        }

        HttpRequestMessage request;
        if (options.Action == "delete")
        {
            request = new HttpRequestMessage(HttpMethod.Delete,
                $"{api.BaseUrl}/v1/models?engine={Uri.EscapeDataString(options.Engine!.Trim())}&model={Uri.EscapeDataString(options.Model!.Trim())}");
        }
        else
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (!string.IsNullOrWhiteSpace(options.Engine))
                    writer.WriteString("engine", options.Engine.Trim());
                if (options.Action == "load" && !string.IsNullOrWhiteSpace(options.Model))
                    writer.WriteString("model", options.Model.Trim());
                writer.WriteEndObject();
            }

            request = new HttpRequestMessage(HttpMethod.Post, $"{api.BaseUrl}/v1/models/{options.Action}")
            {
                Content = new StringContent(Encoding.UTF8.GetString(buffer.ToArray()), Encoding.UTF8, "application/json"),
            };
        }

        var requestBudget = budget ?? (options.Action == "load" ? s_loadBudget : null);
        return QuickApiCommand.RunAsync(api, request, requestBudget, body =>
        {
            var validation = ApiResponseValidator.ValidateModelOperation(body);
            if (validation.Error is not null)
            {
                return ApiResponseValidator.ProtocolError(validation.Error);
            }

            var root = validation.Value!.Root;
            Console.WriteLine(options.Json ? JsonFormatting.PrettyJson(body)
                : $"{ApiResponseValidator.OptionalString(root, "status")}: {ApiResponseValidator.OptionalString(root, "engine")} {ApiResponseValidator.OptionalString(root, "model")}".Trim());
            return ExitCodes.Success;
        }, ct);
    }

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
                    $"Models request failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}",
                    ExitCodes.ServerError
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
            return ConsoleOutput.Error("TypeWhisper is not running or API server is disabled.", ExitCodes.Unavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ConsoleOutput.Error("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            return ConsoleOutput.Error(
                $"The API did not respond within {ConsoleOutput.FormatBudget(requestBudget)}.",
                ExitCodes.Unavailable
            );
        }
        catch (JsonException)
        {
            return ConsoleOutput.Error("Received malformed JSON from the API.", ExitCodes.ServerError);
        }
    }
}