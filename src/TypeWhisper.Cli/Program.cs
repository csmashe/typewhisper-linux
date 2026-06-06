using TypeWhisper.Cli.Commands;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;

namespace TypeWhisper.Cli;

/// <summary>
///     TypeWhisper CLI entry point. Parses arguments, resolves the API
///     port/token (explicit flags win over the auto-discovery file), then
///     dispatches to the matching command. All real work lives in the
///     <see cref="Commands" />, <see cref="Services" />, and <see cref="Output" />
///     namespaces; this file only wires them together.
/// </summary>
public static class Program
{
    private static async Task<int> Main(string[] args)
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
            return ConsoleOutput.Error(options.ErrorMessage);

        if (options.Command is null)
        {
            UsageText.Print();
            return 1;
        }

        // Auto-discovery: pick up port + token from ~/.config/typewhisper/api-discovery.json
        // when neither was explicitly passed. Explicit --port/--token always wins.
        var discovered = DiscoveryFileReader.TryRead();
        var port = options.PortWasExplicit
            ? options.Port
            : discovered?.Port ?? options.Port;
        var token = options.TokenWasExplicit
            ? options.Token
            : options.Token ?? discovered?.Token;

        var api = new ApiClient($"http://127.0.0.1:{port}", token);

        return options.Command switch
        {
            "status" => await StatusCommand.RunAsync(api, options.Json),
            "models" => await ModelsCommand.RunAsync(api, options.Json),
            "transcribe" => await TranscribeCommand.RunAsync(api, options),
            _ => ConsoleOutput.Error($"Unknown command: {options.Command}"),
        };
    }
}
