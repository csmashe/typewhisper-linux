using System.Text.Json;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class HttpApiCorrectionsDtoTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void CorrectionUpsertRequest_Deserializes_SnakeCase()
    {
        var parsed = JsonSerializer.Deserialize<CorrectionUpsertRequest>(
            """{"original":"teh","replacement":"the","case_sensitive":true}""",
            s_options
        );

        Assert.Equal("teh", parsed!.Original);
        Assert.Equal("the", parsed.Replacement);
        Assert.True(parsed.CaseSensitive);
    }

    [Fact]
    public void CorrectionUpsertRequest_MissingFields_ReadsAsNull()
    {
        var parsed = JsonSerializer.Deserialize<CorrectionUpsertRequest>(
            """{"original":"teh"}""",
            s_options
        );

        Assert.Equal("teh", parsed!.Original);
        Assert.Null(parsed.Replacement);
    }

    [Fact]
    public void CorrectionDeleteRequest_Deserializes()
    {
        var parsed = JsonSerializer.Deserialize<CorrectionDeleteRequest>(
            """{"original":"teh"}""",
            s_options
        );

        Assert.Equal("teh", parsed!.Original);
    }
}
