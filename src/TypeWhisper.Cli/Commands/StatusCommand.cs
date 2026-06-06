using System.Text.Json;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli.Commands;

/// <summary>Implements <c>typewhisper status</c>: reports engine/model readiness.</summary>
internal static class StatusCommand
{
    public static async Task<int> RunAsync(ApiClient api, bool json)
    {
        try
        {
            var response = await api.Http.GetAsync($"{api.BaseUrl}/v1/status");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return ConsoleOutput.Error(
                    $"Status request failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}"
                );

            if (json)
            {
                Console.WriteLine(JsonFormatting.PrettyJson(body));
                return 0;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = JsonFormatting.Prop(root, "status") == "ready" ? "Ready" : "No model loaded";
            var engine = JsonFormatting.Prop(root, "engine");
            var model = JsonFormatting.Prop(root, "model");
            Console.WriteLine(
                string.IsNullOrEmpty(model)
                    ? $"{status} - {engine}"
                    : $"{status} - {engine} ({model})"
            );
            return 0;
        }
        catch (HttpRequestException)
        {
            return ConsoleOutput.Error("TypeWhisper is not running or API server is disabled.");
        }
    }
}
