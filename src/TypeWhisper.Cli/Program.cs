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

    internal static async Task<int> RunAsync(string[] args, Func<bool>? isInputRedirected = null)
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
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- a pattern switch on options would hide that these are four independent guard clauses.
        if (options.Command is null)
        {
            UsageText.Print();
            return 1;
        }

        // ReSharper disable once InvertIf -- inverting would nest the dispatch switch below inside this guard.
        if (options is { Command: "transcribe", Positionals.Count: 0 })
        {
            if (!(isInputRedirected?.Invoke() ?? Console.IsInputRedirected))
            {
                return ConsoleOutput.Error("Provide an audio file or pipe audio to stdin.");
            }

            options = options with { Positionals = ["-"] };
        }

        return options.Command switch
        {
            "status" => await WithApiAsync((api, ct) => StatusCommand.RunAsync(api, options.Json, ct)),
            "models" => await WithApiAsync((api, ct) => ModelsCommand.RunAsync(api, options, ct)),
            "history" or "last" => await WithApiAsync((api, ct) => HistoryCommand.RunAsync(api, options, ct)),
            "dictation" => await WithApiAsync((api, ct) => DictationCommand.RunAsync(api, options, ct)),
            "transcribe" => await WithApiAsync((api, ct) => TranscribeCommand.RunAsync(api, options, ct)),
            _ => ConsoleOutput.Error($"Unknown command: {options.Command}"),
        };

        // Resolved per command so an unknown command still reports itself when the
        // app is stopped. The CLI never falls back to TCP: the socket path
        // authenticates the transport before bearer credentials or private audio
        // leave this process.
        async Task<int> WithApiAsync(Func<ApiClient, CancellationToken, Task<int>> run)
        {
            var discovered = DiscoveryFileReader.TryRead();
            if (discovered?.Version is { } version && version != 2)
            {
                return ConsoleOutput.Error(
                    $"The TypeWhisper app wrote discovery protocol version {version}, but this CLI speaks version 2 — app and CLI versions are out of sync.",
                    ExitCodes.Unavailable
                );
            }

            var socketPath = discovered?.SocketPath;
            if (string.IsNullOrWhiteSpace(socketPath))
            {
                return ConsoleOutput.Error(
                    "TypeWhisper API socket not found — is the TypeWhisper app running with the local API enabled?",
                    ExitCodes.Unavailable
                );
            }

            var token = options.TokenWasExplicit
                ? options.Token
                : options.Token ?? discovered?.Token;

            // Install after synchronous, uncancellable discovery so Ctrl+C still
            // terminates normally if discovery blocks.
            var api = new ApiClient(socketPath, token);
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) =>
            {
                // A second Ctrl+C falls through to the default terminate.
                // ReSharper disable AccessToDisposedClosure -- the finally below unsubscribes the handler before the using disposes cts.
                e.Cancel = !cts.IsCancellationRequested;
                cts.Cancel();
                // ReSharper restore AccessToDisposedClosure
            };
            Console.CancelKeyPress += handler;
            try
            {
                return await run(api, cts.Token);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
                api.Http.Dispose();
                api.TranscribeHttp.Dispose();
            }
        }
    }
}