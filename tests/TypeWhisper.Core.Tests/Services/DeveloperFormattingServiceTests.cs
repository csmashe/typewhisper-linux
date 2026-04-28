using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class DeveloperFormattingServiceTests
{
    private readonly DeveloperFormattingService _sut = new();

    [Theory]
    [InlineData("git checkout dash dash force", "git checkout --force")]
    [InlineData("cat file pipe grep error", "cat file|grep error")]
    [InlineData("src slash TypeWhisper dot Core", "src/TypeWhisper.Core")]
    [InlineData("CONFIG underscore PATH equals home slash user", "CONFIG_PATH=home/user")]
    [InlineData("escape backslash n", "escape\\n")]
    public void Format_ReplacesSpokenSymbols(string input, string expected)
    {
        var result = _sut.Format(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("camel case user name", "userName")]
    [InlineData("snake case user name", "user_name")]
    [InlineData("kebab case user name", "user-name")]
    public void Format_AppliesCasingCommands(string input, string expected)
    {
        var result = _sut.Format(input);

        Assert.Equal(expected, result);
    }
}
