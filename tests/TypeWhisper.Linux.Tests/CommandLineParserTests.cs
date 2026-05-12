using TypeWhisper.Linux.Cli;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Empty_args_is_bare_toggle()
    {
        var action = CommandLineParser.Parse(System.Array.Empty<string>());
        Assert.Equal(CliActionKind.BareToggle, action.Kind);
        Assert.False(action.StartMinimized);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--HELP")]
    public void Help_flag_short_circuits(string flag)
    {
        var action = CommandLineParser.Parse(new[] { flag });
        Assert.Equal(CliActionKind.PrintHelp, action.Kind);
    }

    [Fact]
    public void Help_wins_even_when_combined_with_other_flags()
    {
        var action = CommandLineParser.Parse(new[] { "--minimized", "--help" });
        Assert.Equal(CliActionKind.PrintHelp, action.Kind);
    }

    [Fact]
    public void Minimized_flag_launches_gui()
    {
        var action = CommandLineParser.Parse(new[] { "--minimized" });
        Assert.Equal(CliActionKind.LaunchGui, action.Kind);
        Assert.True(action.StartMinimized);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("toggle")]
    [InlineData("cancel")]
    public void Record_verbs_parse(string verb)
    {
        var action = CommandLineParser.Parse(new[] { "record", verb });
        Assert.Equal(CliActionKind.Record, action.Kind);
        Assert.Equal(verb, action.RecordVerb);
    }

    [Fact]
    public void Record_verb_is_case_insensitive()
    {
        var action = CommandLineParser.Parse(new[] { "RECORD", "START" });
        Assert.Equal(CliActionKind.Record, action.Kind);
        Assert.Equal("start", action.RecordVerb);
    }

    [Fact]
    public void Record_without_verb_is_invalid()
    {
        var action = CommandLineParser.Parse(new[] { "record" });
        Assert.Equal(CliActionKind.Invalid, action.Kind);
        Assert.NotNull(action.ErrorMessage);
    }

    [Fact]
    public void Unknown_record_verb_is_invalid()
    {
        var action = CommandLineParser.Parse(new[] { "record", "spin" });
        Assert.Equal(CliActionKind.Invalid, action.Kind);
        Assert.Contains("spin", action.ErrorMessage);
    }

    [Fact]
    public void Status_parses()
    {
        var action = CommandLineParser.Parse(new[] { "status" });
        Assert.Equal(CliActionKind.Status, action.Kind);
    }

    [Fact]
    public void Unknown_subcommand_is_invalid()
    {
        var action = CommandLineParser.Parse(new[] { "nope" });
        Assert.Equal(CliActionKind.Invalid, action.Kind);
    }

    [Fact]
    public void Trailing_operand_after_status_is_invalid()
    {
        var action = CommandLineParser.Parse(new[] { "status", "garbage" });
        Assert.Equal(CliActionKind.Invalid, action.Kind);
    }

    [Fact]
    public void Trailing_operand_after_record_verb_is_invalid()
    {
        var action = CommandLineParser.Parse(new[] { "record", "start", "now" });
        Assert.Equal(CliActionKind.Invalid, action.Kind);
    }

    [Fact]
    public void Trailing_flag_after_record_is_tolerated()
    {
        // Flags after a subcommand are tolerated so wrapper scripts can
        // pass through known/unknown flags without breaking.
        var action = CommandLineParser.Parse(new[] { "record", "start", "--quiet" });
        Assert.Equal(CliActionKind.Record, action.Kind);
        Assert.Equal("start", action.RecordVerb);
    }
}
