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
    [InlineData("function open paren name comma value close paren", "function(name, value)")]
    [InlineData("array open bracket zero close bracket", "array[zero]")]
    [InlineData("object open brace key colon value close brace", "object{key:value}")]
    [InlineData("email at sign example dot com", "email@example.com")]
    [InlineData("2 plus 2 minus 1", "2+2-1")]
    [InlineData("quote hello quote", "\"hello\"")]
    [InlineData("single quote hello single quote", "'hello'")]
    [InlineData("double quote hello double quote", "\"hello\"")]
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

    [Theory]
    [InlineData("dot the i's")]
    [InlineData("see you at 5")]
    [InlineData("price plus tax")]
    [InlineData("Hello, world. How are you?")]
    public void Format_DoesNotCollapseCommonProse(string input)
    {
        var result = _sut.Format(input);

        Assert.Equal(input, result);
    }
}
