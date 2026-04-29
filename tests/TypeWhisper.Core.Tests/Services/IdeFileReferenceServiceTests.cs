using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class IdeFileReferenceServiceTests
{
    private readonly IdeFileReferenceService _sut = new();

    [Theory]
    [InlineData("index dot ts", "index.ts")]
    [InlineData("app settings dot json", "app_settings.json")]
    [InlineData("program dot c sharp", "program.cs")]
    [InlineData("dot env", ".env")]
    [InlineData("my script name", "my_script_name")]
    public void ToFileReference_ConvertsSpokenNames(string input, string expected)
    {
        var result = _sut.ToFileReference(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToAtReference_PrefixesFileReferenceForAiChats()
    {
        var result = _sut.ToAtReference("index dot ts");

        Assert.Equal("@index.ts", result);
    }

    [Theory]
    [InlineData("at index dot ts", "@index.ts")]
    [InlineData("tag program dot c sharp", "@program.cs")]
    [InlineData("file tag dot env", "@.env")]
    [InlineData("file app settings dot json", "app_settings.json")]
    public void TryFormatReferenceCommand_ConvertsClearReferenceCommands(string input, string expected)
    {
        var result = _sut.TryFormatReferenceCommand(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("make this variable camel case")]
    [InlineData("at the end of the day, save this")]
    [InlineData("index dot ts")]
    [InlineData("tag this as urgent")]
    [InlineData("file this under important")]
    [InlineData("reference the spec for details")]
    public void TryFormatReferenceCommand_LeavesNormalDeveloperTextAlone(string input)
    {
        var result = _sut.TryFormatReferenceCommand(input);

        Assert.Null(result);
    }
}
