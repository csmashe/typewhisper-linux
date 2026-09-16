using System.Text.Json;
using TypeWhisper.Cli.Output;

namespace TypeWhisper.Cli.Services;

/// <summary>Runs a quick command within one cancellation and response budget.</summary>
internal static class QuickApiCommand
{
    private static readonly TimeSpan s_defaultBudget = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(
        ApiClient api,
        HttpRequestMessage request,
        TimeSpan? budget,
        Func<string, int> render,
        CancellationToken ct
    )
    {
        var requestBudget = budget ?? s_defaultBudget;
        using (request)
        using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            requestCts.CancelAfter(requestBudget);
            try
            {
                using var response = await api.Http.SendAsync(request, requestCts.Token);
                var body = await response.Content.ReadAsStringAsync(requestCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return ConsoleOutput.Error(
                        $"API request failed ({(int)response.StatusCode}): {JsonFormatting.ExtractErrorMessage(body)}",
                        ExitCodes.ServerError
                    );
                }

                return render(body);
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
                return ApiResponseValidator.ProtocolError("response body is not valid JSON");
            }
        }
    }
}