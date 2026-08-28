using TypeWhisper.Cli.Commands;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli;

/// <summary>
///     TypeWhisper CLI entry point. Parses arguments, resolves the API
///     Unix socket/token (explicit token flags win over auto-discovery), then
///     dispatches to the matching command. All real work lives in the
///     <see cref="Commands" />, <see cref="Services" />, and <see cref="Output" />
///     namespaces; this file only wires them together.
/// </summary>
public static class Program
{
    private static Task<int> Main(string[] args)
    {
        return RunAsync(args);
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            UsageText.Print();
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"typewhisper-cli {VersionInfo.Current}");
            return 0;
        }

        if (options.ErrorMessage is not null)
        {
            return ConsoleOutput.Error(options.ErrorMessage);
        }

        // ReSharper disable once InvertIf -- guard clause, matching the three checks above.
        if (options.Command is null)
        {
            UsageText.Print();
            return 1;
        }

        return options.Command switch
        {
            "status" => await WithQuickApiAsync(
                (api, ct) => StatusCommand.RunAsync(api, options.Json, ct)
            ),
            "models" => await WithQuickApiAsync(
                (api, ct) => ModelsCommand.RunAsync(api, options.Json, ct)
            ),
            "transcribe" => await WithApiAsync(api => TranscribeCommand.RunAsync(api, options)),
            _ => ConsoleOutput.Error($"Unknown command: {options.Command}"),
        };

        // Ctrl+C is intercepted only once the API call is in flight. Installing the
        // handler around discovery would suppress the default terminate while
        // DiscoveryFileReader.TryRead is blocked, and that read is synchronous and
        // uncancellable, so the CLI would stop responding to Ctrl+C entirely.
        Task<int> WithQuickApiAsync(Func<ApiClient, CancellationToken, Task<int>> run)
        {
            return WithApiAsync(async api =>
            {
                using var cts = new CancellationTokenSource();
                ConsoleCancelEventHandler handler = (_, e) =>
                {
                    e.Cancel = true;
                    // ReSharper disable once AccessToDisposedClosure -- the finally below unsubscribes the handler before the using disposes cts.
                    cts.Cancel();
                };
                Console.CancelKeyPress += handler;
                try
                {
                    return await run(api, cts.Token);
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            });
        }

        // Resolved per command so an unknown command still reports itself when the
        // app is stopped. The CLI never falls back to TCP: the socket path
        // authenticates the transport before bearer credentials or private audio
        // leave this process.
        async Task<int> WithApiAsync(Func<ApiClient, Task<int>> run)
        {
            var discovered = DiscoveryFileReader.TryRead();
            if (discovered?.Version is { } version && version != 2)
            {
                return ConsoleOutput.Error(
                    $"The TypeWhisper app wrote discovery protocol version {version}, but this CLI speaks version 2 — app and CLI versions are out of sync."
                );
            }

            var socketPath = discovered?.SocketPath;
            if (string.IsNullOrWhiteSpace(socketPath))
            {
                return ConsoleOutput.Error(
                    "TypeWhisper API socket not found — is the TypeWhisper app running with the local API enabled?"
                );
            }

            var token = options.TokenWasExplicit
                ? options.Token
                : options.Token ?? discovered?.Token;

            return await run(new ApiClient(socketPath, token));
        }
    }
}
