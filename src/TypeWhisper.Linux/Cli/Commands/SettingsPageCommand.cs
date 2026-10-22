using System.Text.Json;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Ipc;

namespace TypeWhisper.Linux.Cli.Commands;

/// <summary>
///     Hands <c>typewhisper --page &lt;page&gt;</c> to a running instance, which shows its
///     window on that page exactly as a first launch would.
/// </summary>
internal static class SettingsPageCommand
{
    /// <summary>
    ///     Returns false when no instance is running, so the caller starts the GUI on the page
    ///     itself. Otherwise the running instance answered or could not be reached, and
    ///     <paramref name="exitCode" /> is the process result.
    /// </summary>
    public static bool TryHandOff(string socketPath, SettingsPage page, out int exitCode)
    {
        var request = new
        {
            v = JsonControlProtocol.CurrentVersion,
            cmd = JsonControlProtocol.CmdSettingsOpen,
            page = page.Name,
        };

        if (!ControlSocketClient.TrySendJson(socketPath, request, out var responseJson, out var error))
        {
            if (error is null)
            {
                exitCode = 0;
                return false;
            }

            Console.Error.WriteLine($"typewhisper: could not reach the running instance: {error}");
            exitCode = 1;
            return true;
        }

        var replyError = ReadError(responseJson);
        exitCode = replyError is null ? 0 : 1;
        if (replyError is not null)
        {
            Console.Error.WriteLine(DescribeError(replyError, page));
        }

        return true;
    }

    /// <summary>
    ///     For a launch that lost the bind: the winner may hold the socket before it listens, so
    ///     keep trying for <paramref name="window" />. Returns the process exit code.
    /// </summary>
    public static int HandOffToStartingInstance(string socketPath, SettingsPage page, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (TryHandOff(socketPath, page, out var exitCode))
            {
                return exitCode;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Console.Error.WriteLine(
                    "typewhisper: another TypeWhisper is starting but did not accept the page; try again"
                );
                return 1;
            }

            Thread.Sleep(100);
        }
    }

    private static string DescribeError(string code, SettingsPage page) =>
        code switch
        {
            // Sent by a running version that predates settings.open.
            JsonControlProtocol.ErrUnknownCommand =>
                "typewhisper: the running TypeWhisper does not support --page; restart it and try again",
            JsonControlProtocol.ErrUnknownPage =>
                $"typewhisper: the running TypeWhisper does not know the page '{page.Name}'",
            _ => $"typewhisper: the running TypeWhisper could not open the page ({code})",
        };

    /// <summary>Null for an <c>ok</c> reply; otherwise the error code, or a placeholder if unreadable.</summary>
    internal static string? ReadError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "malformed-reply";
            }

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "malformed-reply"
                : "malformed-reply";
        }
        catch (JsonException)
        {
            return "malformed-reply";
        }
    }
}
