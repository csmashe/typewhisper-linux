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
            "status" => await WithApiAsync(api => StatusCommand.RunAsync(api, options.Json)),
            "models" => await WithApiAsync(api => ModelsCommand.RunAsync(api, options.Json)),
            "transcribe" => await WithApiAsync(api => TranscribeCommand.RunAsync(api, options)),
            _ => ConsoleOutput.Error($"Unknown command: {options.Command}"),
        };

        // Resolved per command so an unknown command still reports itself when the
        // app is stopped. The CLI never falls back to TCP: the socket path
        // authenticates the transport before bearer credentials or private audio
        // leave this process.
        async Task<int> WithApiAsync(Func<ApiClient, Task<int>> run)
        {
            var discovered = DiscoveryFileReader.TryRead();
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
