using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class FailureMessageSanitizerTests
{
    [Fact]
    public void Sanitize_RemovesAllSecretsAndCollapsesWhitespace()
    {
        var result = FailureMessageSanitizer.Sanitize("bad\n raw text\t final text prompt raw text", "raw text", "final text", "prompt", null, "");
        Assert.Equal("bad [redacted] [redacted] [redacted] [redacted]", result);
    }

    [Fact]
    public void Sanitize_CapsAt300IncludingEllipsis()
    {
        var result = Assert.IsType<string>(FailureMessageSanitizer.Sanitize(new string('x', 400)));
        Assert.Equal(300, result.Length);
        Assert.EndsWith("…", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(57)]
    public void Sanitize_RedactsTruncatedTranscriptWindows(int offset)
    {
        var transcript = string.Concat(Enumerable.Range(0, 100).Select(i => $"{i:D3} "));
        Assert.Equal(400, transcript.Length);
        var message = string.Concat(transcript.AsSpan(offset, 299), "…");
        Assert.Equal("[redacted]…", FailureMessageSanitizer.Sanitize(message, transcript));
    }

    [Theory]
    [InlineData("private\ntext", "private\\ntext")]
    [InlineData("private\"text", "private\\\"text")]
    [InlineData("private\"text", "private\\u0022text")]
    public void Sanitize_RedactsJsonEscapedSecret(string secret, string escaped)
    {
        var message = "{\"message\":\"" + escaped + "\"}";
        Assert.Equal("{\"message\":\"[redacted]\"}", FailureMessageSanitizer.Sanitize(message, secret));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(57)]
    public void Sanitize_RedactsTruncatedJsonEscapedWindows(int offset)
    {
        var secret = string.Concat(Enumerable.Range(0, 100).Select(i => $"{i:D3}\n"));
        var escaped = System.Text.Json.JsonEncodedText.Encode(secret).ToString();
        var message = string.Concat(escaped.AsSpan(offset, 299), "…");
        Assert.Equal("[redacted]…", FailureMessageSanitizer.Sanitize(message, secret));
    }

    [Fact]
    public void Sanitize_LeavesShortSharedPhraseAlone()
    {
        const string message = "Provider could not process the request";
        Assert.Equal(message, FailureMessageSanitizer.Sanitize(message,
            "Please process the request and then send the full results to my team."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t")]
    public void Sanitize_BlankReturnsNullForCallerToLocalize(string? message) =>
        Assert.Null(FailureMessageSanitizer.Sanitize(message));
}
