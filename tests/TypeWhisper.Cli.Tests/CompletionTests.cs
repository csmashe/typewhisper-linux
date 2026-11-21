using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using TypeWhisper.Cli.Models;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class CompletionTests : IDisposable
{
    private const string ModelsJson =
        """
        {"models":[
          {"id":"tiny","engine":"whisper-cpp"},
          {"id":"base","engine":"whisper-cpp"},
          {"id":"parakeet-tdt-0.6b","engine":"sherpa-onnx"},
          {"id":"tiny","engine":"whisper-cpp"},
          {"id":"has space","engine":"bad engine"},
          {"id":"x;rm","engine":"whisper-cpp"},
          {"id":"line\nbreak","engine":"whisper-cpp"},
          {"engine":"cloud"},
          3
        ]}
        """;

    private readonly string _configHome =
        Path.Join(Path.GetTempPath(), "typewhisper-cli-completion-" + Guid.NewGuid().ToString("N"));

    private readonly string? _originalConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public CompletionTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalConfigHome);
        if (Directory.Exists(_configHome))
        {
            Directory.Delete(_configHome, recursive: true);
        }
    }

    [Theory]
    [InlineData("bash", "complete -F _typewhisper_cli typewhisper-cli")]
    [InlineData("zsh", "compdef _typewhisper_cli typewhisper-cli")]
    [InlineData("fish", "complete -c typewhisper-cli -f -a '(__typewhisper_cli_complete)'")]
    public void Script_registers_for_the_cli_name_with_every_token_filled(string shell, string registration)
    {
        var script = CliCompletions.Script(shell);

        Assert.NotNull(script);
        Assert.Contains(registration, script, StringComparison.Ordinal);
        Assert.DoesNotMatch("##[A-Z_]+##", script);
        Assert.DoesNotContain('\r', script);
        Assert.DoesNotContain("typewhisper.exe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Zsh_script_starts_with_compdef_so_it_can_live_in_fpath()
    {
        Assert.StartsWith("#compdef typewhisper-cli\n", CliCompletions.Script("zsh"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("tcsh")]
    [InlineData("")]
    public void Script_is_null_for_unsupported_shells(string shell)
    {
        Assert.Null(CliCompletions.Script(shell));
    }

    [Fact]
    public void Bash_script_parses()
    {
        var result = RunBash("bash -n /dev/stdin", CliCompletions.Script("bash")!);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "", "words status models history last dictation transcribe completions")]
    [InlineData("st", "", "words status models history last dictation transcribe completions")]
    [InlineData("", "models", "words list load unload delete")]
    [InlineData("", "history", "words search last")]
    [InlineData("", "dictation", "words start stop status result")]
    [InlineData("", "completions", "words bash zsh fish")]
    [InlineData("", "transcribe", "files")]
    [InlineData("", "transcribe --language de", "files")]
    [InlineData("", "--json transcribe", "files")]
    [InlineData("-", "transcribe --", "files")]
    [InlineData("", "transcribe --task", "words transcribe translate")]
    [InlineData("", "transcribe --response-format", "words json verbose_json text srt vtt")]
    [InlineData("", "transcribe --language", "none")]
    [InlineData("", "transcribe recording.wav", "none")]
    [InlineData("", "status", "none")]
    [InlineData("--", "status", "words --token --api-token --json --help --version")]
    [InlineData("--", "models unload", "words --engine --token --api-token --json --help --version")]
    [InlineData("--", "history search", "words --limit --offset --token --api-token --json --help --version")]
    public void Candidates_follow_the_command_line(string current, string before, string expected)
    {
        Assert.Equal(expected, string.Join(' ', Candidates(current, before)));
    }

    // COMP_WORDS ('|'-separated) as readline splits them: audio=t arrives as audio, = and t, and
    // bash replaces only the text after the last split.
    [Theory]
    [InlineData("typewhisper-cli transcribe audio=t", "typewhisper-cli|transcribe|audio|=|t", "take.wav")]
    [InlineData("typewhisper-cli transcribe has\\ space=t", "typewhisper-cli|transcribe|has\\ space|=|t", "take.wav")]
    [InlineData("typewhisper-cli transcribe \"has space=t", "typewhisper-cli|transcribe|\"has space=t", "has space=take.wav")]
    [InlineData("typewhisper-cli transcribe --prompt key=value ex", "typewhisper-cli|transcribe|--prompt|key|=|value|ex", "example.wav")]
    [InlineData("typewhisper-cli transcribe --prompt a:b ex", "typewhisper-cli|transcribe|--prompt|a|:|b|ex", "example.wav")]
    [InlineData("typewhisper-cli m", "typewhisper-cli|m", "models")]
    // Without ':' in COMP_WORDBREAKS a leading ':' is part of the word and is not stripped.
    [InlineData("typewhisper-cli transcribe :", "typewhisper-cli|transcribe|:", ":take.wav", " \t\n\"'><=;|&(")]
    [InlineData("typewhisper-cli transcribe :", "typewhisper-cli|transcribe|:", "take.wav")]
    public void Bash_glue_rejoins_words_bash_split_at_word_breaks(
        string line, string words, string expected, string wordBreaks = " \t\n\"'><=;|&(:")
    {
        var directory = Path.Join(_configHome, "files");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "audio=take.wav"), "");
        File.WriteAllText(Path.Join(directory, "has space=take.wav"), "");
        File.WriteAllText(Path.Join(directory, "example.wav"), "");
        File.WriteAllText(Path.Join(directory, ":take.wav"), "");

        var result = RunBash(
            $"cd {Quote(directory)} && source /dev/stdin <<'TW_SCRIPT'\n{CliCompletions.Script("bash")}TW_SCRIPT\n"
            + "compopt() { :; }\n"
            + $"COMP_WORDBREAKS={Quote(wordBreaks)}\n"
            + $"COMP_LINE={Quote(line)}; COMP_POINT=${{#COMP_LINE}}\n"
            + $"COMP_WORDS=({string.Join(' ', words.Split('|').Select(Quote))})\n"
            + "COMP_CWORD=$((${#COMP_WORDS[@]} - 1))\n"
            + "_typewhisper_cli; printf '%s\\n' \"${COMPREPLY[@]}\"",
            "");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected + "\n", result.Output);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Engine_and_model_values_come_from_the_helper_with_the_engine_and_token_on_the_line()
    {
        var helper = Path.Join(_configHome, "fake-cli");
        Directory.CreateDirectory(_configHome);
        File.WriteAllText(helper, "#!/bin/sh\nprintf 'id\\n'; printf '%s\\n' \"$*\"\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Equal(
            ["words", "id", "__complete engines --token t"],
            Candidates("", "--token t transcribe --engine", helper));
        Assert.Equal(
            ["words", "id", "__complete models --engine whisper-cpp"],
            Candidates("", "models load --engine whisper-cpp --model", helper));
    }

    [Fact]
    public void Every_offered_option_is_accepted_where_it_is_offered()
    {
        string[][] contexts =
        [
            [], ["status"], ["last"], ["transcribe"], ["history"], ["history", "search", "q"],
            ["models", "list"], ["models", "load"], ["models", "unload"], ["models", "delete"],
            ["dictation", "start"], ["completions", "bash"],
        ];
        foreach (var context in contexts)
        {
            var offered = Candidates("--", string.Join(' ', context)).Skip(1).ToArray();
            Assert.NotEmpty(offered);
            foreach (var option in offered.Where(option => option is not ("--help" or "--version")))
            {
                string[] args = [.. context, option, .. ValueFor(option)];
                var error = CliOptions.Parse(args).ErrorMessage ?? "";
                Assert.DoesNotContain("is not valid", error, StringComparison.Ordinal);
                Assert.DoesNotContain("Unknown option", error, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Ids_returns_each_safe_engine_once()
    {
        Assert.Equal(["whisper-cpp", "sherpa-onnx", "cloud"], CliCompletions.Ids(ModelsJson, engines: true));
    }

    [Fact]
    public void Ids_returns_safe_model_ids_optionally_for_one_engine()
    {
        Assert.Equal(["tiny", "base", "parakeet-tdt-0.6b"], CliCompletions.Ids(ModelsJson, engines: false));
        Assert.Equal(["tiny", "base"], CliCompletions.Ids(ModelsJson, engines: false, engine: " Whisper-CPP "));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"models":{}}""")]
    public void Ids_is_empty_for_other_shapes(string json)
    {
        Assert.Empty(CliCompletions.Ids(json, engines: true));
    }

    [Fact]
    public async Task Completions_command_prints_the_script()
    {
        var result = await RunCliAsync(["completions", "fish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CliCompletions.Script("fish"), result.Output);
        Assert.Equal("", result.Error);
    }

    [Theory]
    [InlineData("completions")]
    [InlineData("completions tcsh")]
    [InlineData("completions bash zsh")]
    public async Task Completions_command_rejects_a_missing_or_unknown_shell(string args)
    {
        var result = await RunCliAsync(args.Split(' '));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage: typewhisper-cli completions <bash|zsh|fish>", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("completions bash --engine e", "Option '--engine' is not valid for 'completions'.")]
    public async Task Completions_command_rejects_options(string args, string message)
    {
        var result = await RunCliAsync(args.Split(' '));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Helper_prints_ids_from_the_running_app_one_per_line()
    {
        await using var stub = new UnixHttpStub(responseBody: ModelsJson);
        WriteDiscovery(stub.SocketPath, "discovered-token");

        var engines = await RunCliAsync(["__complete", "engines"]);
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var models = await RunCliAsync(["__complete", "models", "--engine", "sherpa-onnx", "--token", "explicit"]);

        Assert.Equal((0, "whisper-cpp\nsherpa-onnx\ncloud\n", ""), engines);
        Assert.Equal("GET /v1/models HTTP/1.1", request.RequestLine);
        Assert.Equal("Bearer discovered-token", request.Headers["Authorization"]);
        Assert.Equal((0, "parakeet-tdt-0.6b\n", ""), models);
    }

    [Theory]
    [InlineData("__complete")]
    [InlineData("__complete engines")]
    [InlineData("__complete voices")]
    [InlineData("__complete models extra")]
    [InlineData("__complete models --engine")]
    public async Task Helper_is_silent_and_succeeds_without_an_app_or_with_bad_input(string args)
    {
        Assert.Equal((0, "", ""), await RunCliAsync(args.Split(' ')));
    }

    [Fact]
    public async Task Helper_is_silent_when_the_app_answers_with_an_error_or_garbage()
    {
        await using (var stub = new UnixHttpStub(System.Net.HttpStatusCode.Unauthorized, """{"error":"no"}"""))
        {
            WriteDiscovery(stub.SocketPath, "t");
            Assert.Equal((0, "", ""), await RunCliAsync(["__complete", "engines"]));
        }

        await using (var stub = new UnixHttpStub(responseBody: "not json"))
        {
            WriteDiscovery(stub.SocketPath, "t");
            Assert.Equal((0, "", ""), await RunCliAsync(["__complete", "engines"]));
        }
    }

    [Fact]
    public async Task Helper_gives_up_quickly_when_the_app_stalls()
    {
        await using var stub = new UnixHttpStub(responseBody: ModelsJson) { StallResponse = true };
        WriteDiscovery(stub.SocketPath, "t");

        var watch = Stopwatch.StartNew();
        var result = await RunCliAsync(["__complete", "engines"]);

        Assert.Equal((0, "", ""), result);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private static string[] ValueFor(string option)
    {
        return option switch
        {
            "--limit" or "--offset" => ["1"],
            "--task" => ["transcribe"],
            "--response-format" => ["json"],
            "--token" or "--api-token" or "--language" or "--language-hint" or "--translate-to" or "--prompt"
                or "--engine" or "--model" or "--query" => ["x"],
            _ => [],
        };
    }

    // Calls the shared bash/zsh candidates function: kind first, then the candidate words.
    private static string[] Candidates(string current, string before, string executable = "typewhisper-cli")
    {
        var words = before.Length == 0 ? "" : " " + string.Join(' ', before.Split(' ').Select(Quote));
        var result = RunBash(
            $"source /dev/stdin <<'TW_SCRIPT'\n{CliCompletions.Script("bash")}TW_SCRIPT\n"
            + $"__typewhisper_cli_candidates {Quote(executable)} {Quote(current)}{words}",
            "");
        Assert.Equal(0, result.ExitCode);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Quote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static (int ExitCode, string Output) RunBash(string command, string input)
    {
        var start = new ProcessStartInfo("bash", ["--noprofile", "--norc", "-c", command])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(error.Length == 0, error);
        return (process.ExitCode, output);
    }

    private void WriteDiscovery(string socketPath, string token)
    {
        var directory = Path.Join(_configHome, "typewhisper");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Join(directory, "api-discovery.json"),
            JsonSerializer.Serialize(new { version = 2, port = 9876, token, socket_path = socketPath }));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.RunAsync(args);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
