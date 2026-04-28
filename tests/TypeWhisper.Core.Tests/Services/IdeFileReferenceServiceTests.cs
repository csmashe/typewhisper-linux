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
}
