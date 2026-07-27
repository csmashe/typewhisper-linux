using System.Text.RegularExpressions;
using TypeWhisper.Cli.Output;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed partial class UsageTextTests
{
    [Fact]
    public void Print_uses_distinct_cli_command_name()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);
            UsageText.Print();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        var exampleLines = output
            .Split(Environment.NewLine)
            .Select(line => line.TrimStart())
            .Where(line => line.StartsWith("typewhisper", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains("Usage: typewhisper-cli <command> [options]", output);
        Assert.DoesNotContain("--port", output, StringComparison.Ordinal);
        Assert.NotEmpty(exampleLines);
        Assert.All(
            exampleLines,
            line => Assert.StartsWith("typewhisper-cli ", line, StringComparison.Ordinal)
        );
        Assert.DoesNotMatch(BareCommandNameRegex(), output);
    }

    [GeneratedRegex(@"(?m)(?:^|\s)typewhisper(?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex BareCommandNameRegex();
}
