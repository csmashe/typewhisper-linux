using System.Text.Json;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

/// <summary>Implements <c>typewhisper models</c>: lists available models as a table or JSON.</summary>
internal static class ModelsCommand
{
    public static async Task<int> RunAsync(ApiClient api, bool json)
    {
        try
        {
            var response = await api.Http.GetAsync($"{api.BaseUrl}/v1/models");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return ConsoleOutput.Error(
                    $"Models request failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}"
                );
            }

            if (json)
            {
                Console.WriteLine(JsonFormatting.PrettyJson(body));
                return 0;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("models", out var models))
            {
                Console.Error.WriteLine(
                    "Warning: response is missing the 'models' field; the API contract may have changed."
                );
                return 0;
            }

            var rows = models.EnumerateArray().ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine("No models available.");
                return 0;
            }

            var idWidth = Math.Max(2, rows.Max(m => JsonFormatting.Prop(m, "id").Length));
            var engineWidth = Math.Max(6, rows.Max(m => JsonFormatting.Prop(m, "engine").Length));
            var nameWidth = Math.Max(4, rows.Max(m => JsonFormatting.Prop(m, "name").Length));

            Console.WriteLine(
                $"{ConsoleOutput.Pad("ID", idWidth)}  {ConsoleOutput.Pad("ENGINE", engineWidth)}  {ConsoleOutput.Pad("NAME", nameWidth)}  STATUS"
            );
            Console.WriteLine(new string('-', idWidth + engineWidth + nameWidth + 12));

            foreach (var m in rows)
            {
                var selected =
                    m.TryGetProperty("selected", out var sel) && sel.GetBoolean() ? " *" : "";
                Console.WriteLine(
                    $"{ConsoleOutput.Pad(JsonFormatting.Prop(m, "id"), idWidth)}  {ConsoleOutput.Pad(JsonFormatting.Prop(m, "engine"), engineWidth)}  {ConsoleOutput.Pad(JsonFormatting.Prop(m, "name"), nameWidth)}  {JsonFormatting.Prop(m, "status")}{selected}"
                );
            }

            return 0;
        }
        catch (HttpRequestException)
        {
            return ConsoleOutput.Error("TypeWhisper is not running or API server is disabled.");
        }
        catch (JsonException)
        {
            return ConsoleOutput.Error("Received malformed JSON from the API.");
        }
    }
}