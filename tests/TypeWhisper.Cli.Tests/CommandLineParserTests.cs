using TypeWhisper.Cli.Models;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public class CommandLineParserTests
{
    public static TheoryData<string, string[]> TranscribeOnlyOptionCases =>
        new()
        {
            { "--language", ["--language", "de"] },
            { "--language-hint", ["--language-hint", "de"] },
            { "--task", ["--task", "translate"] },
            { "--translate-to", ["--translate-to", "fr"] },
            { "--response-format", ["--response-format", "verbose_json"] },
            { "--prompt", ["--prompt", "Project names"] },
            { "--engine", ["--engine", "groq"] },
            { "--model", ["--model", "whisper-large-v3"] },
            { "--await-download", ["--await-download"] },
        };

    [Fact]
    public void Port_IsAnUnknownOption()
    {
        var options = CliOptions.Parse(["--port", "8080", "status"]);

        Assert.NotNull(options.ErrorMessage);
        Assert.Equal("Unknown option '--port'.", options.ErrorMessage);
    }

    [Fact]
    public void Token_FollowedByFlag_FailsCleanly()
    {
        var options = CliOptions.Parse(["--token", "--json", "status"]);
        Assert.NotNull(options.ErrorMessage);
        Assert.Contains("requires a value", options.ErrorMessage);
    }

    [Fact]
    public void ApiTokenAlias_MapsToToken()
    {
        var options = CliOptions.Parse(["--api-token", "abc123", "status"]);
        Assert.Null(options.ErrorMessage);
        Assert.Equal("abc123", options.Token);
        Assert.True(options.TokenWasExplicit);
        Assert.Equal("status", options.Command);
    }

    [Theory]
    [MemberData(nameof(TranscribeOnlyOptionCases))]
    public void TranscribeOnlyOptions_AreRejectedForStatusAndModels(
        string option,
        string[] optionArgs
    )
    {
        foreach (var command in new[] { "status", "models" })
        {
            var beforeCommand = CliOptions.Parse([.. optionArgs, command]);
            var afterCommand = CliOptions.Parse([command, .. optionArgs]);

            var expected = $"Option '{option}' is not valid for '{command}'.";
            Assert.Equal(expected, beforeCommand.ErrorMessage);
            Assert.Equal(expected, afterCommand.ErrorMessage);
        }
    }

    [Theory]
    [MemberData(nameof(TranscribeOnlyOptionCases))]
    public void TranscribeOnlyOptions_AreAcceptedForTranscribe(
        string _,
        string[] optionArgs
    )
    {
        var options = CliOptions.Parse(["transcribe", "audio.wav", .. optionArgs]);

        Assert.Null(options.ErrorMessage);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("models")]
    [InlineData("transcribe")]
    public void SurplusOperands_AreRejected(string command)
    {
        var args =
            command == "transcribe"
                ? new[] { command, "audio.wav", "surplus.wav" }
                : [command, "surplus.wav"];

        var options = CliOptions.Parse(args);

        Assert.Equal($"Unexpected operand 'surplus.wav' for '{command}'.", options.ErrorMessage);
    }

    [Fact]
    public void Transcribe_RequiresExactlyOneOperand()
    {
        var options = CliOptions.Parse(["transcribe"]);

        Assert.Equal(
            "Command 'transcribe' requires exactly one file operand.",
            options.ErrorMessage
        );
    }

    [Fact]
    public void EndOfOptions_AllowsDashLeadingFileOperand()
    {
        var options = CliOptions.Parse(["transcribe", "--", "-recording.wav"]);

        Assert.Null(options.ErrorMessage);
        Assert.Equal(["-recording.wav"], options.Positionals);
    }

    [Fact]
    public void BareDash_RemainsAValidStdinOperand()
    {
        var options = CliOptions.Parse(["transcribe", "-"]);

        Assert.Null(options.ErrorMessage);
        Assert.Equal(["-"], options.Positionals);
    }

    [Theory]
    [InlineData("translate")]
    [InlineData("TRANSLATE")]
    [InlineData(" translate ")]
    public void Task_IsNormalized(string value)
    {
        var options = CliOptions.Parse(["transcribe", "audio.wav", "--task", value]);

        Assert.Null(options.ErrorMessage);
        Assert.Equal("translate", options.Task);
    }

    [Fact]
    public void UnknownTask_ProducesNamedError()
    {
        var options = CliOptions.Parse([
            "transcribe",
            "audio.wav",
            "--task",
            "transalte",
        ]);

        Assert.Equal(
            "Invalid value 'transalte' for --task. Allowed values: transcribe, translate.",
            options.ErrorMessage
        );
    }

    [Theory]
    [InlineData("verbose_json")]
    [InlineData("Verbose_JSON")]
    [InlineData(" verbose_json ")]
    public void ResponseFormat_IsNormalized(string value)
    {
        var options = CliOptions.Parse([
            "transcribe",
            "audio.wav",
            "--response-format",
            value,
        ]);

        Assert.Null(options.ErrorMessage);
        Assert.Equal("verbose_json", options.ResponseFormat);
    }

    [Fact]
    public void UnknownResponseFormat_ProducesNamedError()
    {
        var options = CliOptions.Parse([
            "transcribe",
            "audio.wav",
            "--response-format",
            "xml",
        ]);

        Assert.Equal(
            "Invalid value 'xml' for --response-format. Allowed values: json, verbose_json.",
            options.ErrorMessage
        );
    }

    [Fact]
    public void UnknownCommand_IsLeftForProgramDispatch()
    {
        var options = CliOptions.Parse(["future-command", "operand"]);

        Assert.Null(options.ErrorMessage);
        Assert.Equal("future-command", options.Command);
        Assert.Equal(["operand"], options.Positionals);
    }
}
