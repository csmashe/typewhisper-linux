using System.Diagnostics;
using System.Text.Json;

namespace TypeWhisper.FakeProviderCli;

public static class FakeProviderCliMarker;

/// <summary>
///     Stands in for a signed-in provider CLI. The plugin discovers CLIs by file name on PATH,
///     so tests install a shell shim named <c>codex</c>/<c>claude</c>/<c>opencode</c> that execs
///     this assembly; the shim exports which provider and scenario it is playing, because the
///     plugin clears the environment and rebuilds it from an allow-list, so nothing the test
///     process sets survives into this one.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--child", StringComparer.Ordinal))
        {
            await File.WriteAllTextAsync(
                Path.Join(Environment.CurrentDirectory, "child.pid"),
                Environment.ProcessId.ToString()
            );
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        var provider = Environment.GetEnvironmentVariable("TYPEWHISPER_FAKE_PROVIDER");
        if (string.IsNullOrEmpty(provider))
        {
            // Guessing the provider would silently answer in the wrong shape; the shim always
            // exports it, so its absence is a broken fixture.
            await Console.Error.WriteLineAsync(
                "TYPEWHISPER_FAKE_PROVIDER is not set; the fake provider CLI must be launched through a test shim."
            );
            return 64;
        }

        var scenario = Environment.GetEnvironmentVariable("TYPEWHISPER_FAKE_SCENARIO") ?? "success";
        var captureDirectory = Environment.GetEnvironmentVariable("TYPEWHISPER_FAKE_DIR")
                               ?? AppContext.BaseDirectory;

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine($"{provider} 2.1.205");
            return 0;
        }

        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine(
                "--ignore-user-config --ignore-rules --ephemeral --output-schema --strict-config --json --sandbox --skip-git-repo-check"
            );
            Console.WriteLine(
                "--safe-mode --tools --strict-mcp-config --no-session-persistence --json-schema --disallowedTools --disable-slash-commands --no-chrome"
            );
            Console.WriteLine("--pure --model --agent --format --title --dir");
            return 0;
        }

        if (args.SequenceEqual(["auth", "list"], StringComparer.Ordinal))
        {
            return OpenCodeAuthList(scenario);
        }

        if (args.SequenceEqual(["login", "status"], StringComparer.Ordinal)
            || args.SequenceEqual(["auth", "status"], StringComparer.Ordinal))
        {
            return AuthStatus(provider, scenario);
        }

        if (args.SequenceEqual(["models", "opencode", "--verbose", "--pure"], StringComparer.Ordinal))
        {
            return ModelCatalog(scenario);
        }

        var standardInput = await Console.In.ReadToEndAsync();
        var capture = new
        {
            arguments = args,
            standardInput,
            workingDirectory = Environment.CurrentDirectory,
            // Named so a test can prove no shell expanded the request: a shell would have run
            // the payload here, before this process was reached.
            workingDirectoryEntries = Directory.GetFileSystemEntries(Environment.CurrentDirectory)
                .Select(Path.GetFileName)
                .ToArray(),
            environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString()),
        };
        await File.WriteAllTextAsync(
            Path.Join(captureDirectory, "capture.json"),
            JsonSerializer.Serialize(capture)
        );

        return await RunRequestScenarioAsync(provider, scenario);
    }

    private static int OpenCodeAuthList(string scenario)
    {
        if (scenario.Contains("signed-out", StringComparison.Ordinal)
            || scenario.Contains("opencode-auth-error", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("No OpenCode Zen credentials");
            return 1;
        }

        if (scenario.Contains("opencode-auth-missing", StringComparison.Ordinal))
        {
            Console.WriteLine("GitHub Copilot");
            return 0;
        }

        var authentication = scenario.Contains("opencode-auth-ansi", StringComparison.Ordinal)
            ? "\e[32mOpenCode Zen\e[0m"
            : "OpenCode Zen";
        if (scenario.Contains("opencode-auth-stderr", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(authentication);
        }
        else
        {
            Console.WriteLine(authentication);
        }

        return 0;
    }

    private static int AuthStatus(string provider, string scenario)
    {
        if (scenario.Contains("signed-out", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("not logged in");
            return 1;
        }

        if (scenario.Contains("auth-unknown", StringComparison.Ordinal))
        {
            Console.WriteLine("{\"loggedIn\":false}");
            return 0;
        }

        Console.WriteLine(provider.Contains("claude", StringComparison.Ordinal)
            ? "{\"loggedIn\":true,\"authMethod\":\"subscription\"}"
            : "Logged in using subscription");
        return 0;
    }

    private static int ModelCatalog(string scenario)
    {
        if (scenario.Contains("catalog-fail", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("catalog unavailable");
            return 2;
        }

        if (scenario.Contains("catalog-several", StringComparison.Ordinal))
        {
            WriteModel("alpha-free", "Alpha Free", inputCost: 0, outputCost: 0, cacheCost: 0);
            WriteModel("zeta-free", "Zeta Free", inputCost: 0, outputCost: 0, cacheCost: 0);
        }

        WriteModel("paid-model", "Paid Model", inputCost: 1, outputCost: 2, cacheCost: 0);
        if (!scenario.Contains("catalog-none", StringComparison.Ordinal))
        {
            WriteModel(
                "muse-spark-1.3-contributor-free",
                "Muse Spark 1.3 Free",
                inputCost: 0,
                outputCost: 0,
                cacheCost: 0
            );
        }

        return 0;
    }

    private static async Task<int> RunRequestScenarioAsync(string provider, string scenario)
    {
        if (scenario.Contains("invalid-json", StringComparison.Ordinal))
        {
            Console.WriteLine("{not-json");
            return 0;
        }

        if (scenario.Contains("invalid-utf8", StringComparison.Ordinal))
        {
            await using var output = Console.OpenStandardOutput();
            await output.WriteAsync(new byte[] { 0xff, 0xfe, 0xfd });
            await output.FlushAsync();
            return 0;
        }

        if (scenario.Contains("timeout", StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        if (scenario.Contains("crash", StringComparison.Ordinal))
        {
            return 42;
        }

        if (scenario.Contains("huge-output", StringComparison.Ordinal))
        {
            Console.Write(new string('x', 1024 * 1024 + 8192));
            await Console.Error.WriteAsync(new string('e', 64 * 1024));
            return 0;
        }

        if (scenario.Contains("child", StringComparison.Ordinal))
        {
            var shim = Environment.GetEnvironmentVariable("TYPEWHISPER_FAKE_SHIM")!;
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = shim,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                ArgumentList = { "--child" },
            });
            await File.WriteAllTextAsync(
                Path.Join(Environment.CurrentDirectory, "parent.pid"),
                Environment.ProcessId.ToString()
            );
            await File.WriteAllTextAsync(
                Path.Join(Environment.CurrentDirectory, "spawned.pid"),
                child!.Id.ToString()
            );
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        if (scenario.Contains("auth-error", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("not logged in; login required");
            return 3;
        }

        if (scenario.Contains("rate-limit", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("rate limit exceeded");
            return 4;
        }

        if (scenario.Contains("network-auth-model", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync(
                "authentication service unreachable while resolving model"
            );
            return 5;
        }

        if (scenario.Contains("stderr", StringComparison.Ordinal))
        {
            await Console.Error.WriteAsync(new string('w', 48 * 1024));
        }

        var logicalResult = JsonSerializer.Serialize(new { text = "processed" });
        if (provider.Contains("codex", StringComparison.Ordinal))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "item.completed",
                item = new { type = "agent_message", text = logicalResult },
            }));
            Console.WriteLine("{\"type\":\"turn.completed\"}");
        }
        else if (provider.Contains("claude", StringComparison.Ordinal))
        {
            Console.WriteLine(
                "{\"type\":\"result\",\"subtype\":\"success\",\"structured_output\":{\"text\":\"processed\"}}"
            );
        }
        else
        {
            Console.WriteLine("{\"type\":\"step_start\",\"part\":{\"type\":\"step-start\"}}");
            Console.WriteLine(
                "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"processed\\\"}\"}}"
            );
        }

        return 0;
    }

    private static void WriteModel(
        string id,
        string name,
        double inputCost,
        double outputCost,
        double cacheCost
    )
    {
        Console.WriteLine($"opencode/{id}");
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                id,
                providerID = "opencode",
                name,
                status = "active",
                capabilities = new
                {
                    input = new { text = true, image = false },
                    output = new { text = true, image = false },
                },
                cost = new
                {
                    input = inputCost,
                    output = outputCost,
                    cache = new { read = cacheCost, write = 0 },
                },
                variants = new { low = new { }, high = new { } },
            },
            s_indentedJson
        ));
    }
}
