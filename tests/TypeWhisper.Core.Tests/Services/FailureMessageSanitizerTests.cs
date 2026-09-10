using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed partial class FailureMessageSanitizerTests
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [Fact]
    public void Sanitize_CascadingRedactionsAcrossPrefixBoundary_MatchFullMessage()
    {
        string[] secrets = [new('Z', 24), new string('Y', 14) + "[redacted]", new string('X', 14) + "[redacted]"];
        var message = new string('a', 280) + new string(' ', 1717)
            + new string('X', 14) + new string('Y', 14) + new string('Z', 24);
        Assert.Equal(new string('a', 280) + " [redacted]", FailureMessageSanitizer.Sanitize(message, secrets));
    }

    [Fact]
    public void Sanitize_ExpandingRedactionsAcrossPrefixBoundary_MatchFullMessage()
    {
        string[] secrets = [new('d', 24), "d"];
        var message = new string(' ', 2025) + new string('d', 24);
        // Whole form → [redacted]; d pass expands two d's; whitespace trims; normalized d pass expands four d's.
        Assert.Equal("[re[re[redacted]acte[redacted]]acte[re[redacted]acte[redacted]]]",
            FailureMessageSanitizer.Sanitize(message, secrets));
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void Sanitize_MatchesNaiveReference(string message, string[] secrets)
    {
        Assert.Equal(SanitizeNaive(message, secrets), FailureMessageSanitizer.Sanitize(message, secrets));
    }

    [Fact]
    public void Sanitize_RepetitiveMessageWithLongNearMatch_RedactsEntireMessage()
    {
        var message = new string('a', 200_000);
        var secret = new string('a', 5_000) + "b";
        Assert.Equal("[redacted]", FailureMessageSanitizer.Sanitize(message, secret));
    }

    [Fact]
    public void Sanitize_RepeatedSecretWithShiftedPartialCopies_MatchesNaiveReference()
    {
        var secret = string.Concat(Enumerable.Repeat("abc", 20));
        var message = string.Concat(
            string.Join(" ", Enumerable.Range(0, 3).Select(offset => secret.Substring(offset, 30))),
            " ",
            secret.AsSpan(1, 45));
        Assert.Equal(SanitizeNaive(message, secret), FailureMessageSanitizer.Sanitize(message, secret));
    }

    [Fact]
    public void Sanitize_JsonEscapedWholeAndPartialSecret_MatchesNaiveReference()
    {
        var secret = string.Concat(Enumerable.Range(0, 20).Select(index => $"{index:D2}\"\n"));
        var escaped = JsonEncodedText.Encode(secret).ToString();
        var message = "{\"message\":\"" + escaped + " " + escaped.Substring(7, 60) + "\"}";
        Assert.Equal(SanitizeNaive(message, secret), FailureMessageSanitizer.Sanitize(message, secret));
    }

    public static TheoryData<string, string[]> ReferenceCases => new()
    {
        {
            new string('a', 280) + new string(' ', 1717)
                + new string('X', 14) + new string('Y', 14) + new string('Z', 24),
            [new string('Z', 24), new string('Y', 14) + "[redacted]", new string('X', 14) + "[redacted]"]
        },
        { new string(' ', 2025) + new string('d', 24), [new string('d', 24), "d"] },
        { new string('a', 200), [new string('a', 40)] },
        { string.Concat(Enumerable.Repeat("abc", 25)), [string.Concat(Enumerable.Repeat("abc", 16))] },
    };

    [Fact]
    public void Sanitize_LargeCollapsedPrefixStillRedactsLateSecret()
    {
        const string secret = "private transcript marker";
        var message = "failure " + new string(' ', 1536 * 1024) + secret + new string(' ', 512 * 1024);
        Assert.Equal("failure [redacted]", FailureMessageSanitizer.Sanitize(message, secret));
    }

    [Fact]
    public void Sanitize_RedactsSecretAcrossInitialPrefixBoundary()
    {
        const string secret = "private transcript marker spanning the boundary";
        var message = "failure " + new string(' ', 2030) + secret + " end";
        Assert.Equal("failure [redacted] end", FailureMessageSanitizer.Sanitize(message, secret));
    }

    [Fact]
    public void Sanitize_LargeMessageWithLateSecret_CapsResult()
    {
        var secret = string.Concat(Enumerable.Range(0, 50).Select(i => $"{i:D3}|"));
        var message = new string('x', 3 * 1024 * 1024) + secret
            + new string('x', 1024 * 1024 - secret.Length);
        var result = Assert.IsType<string>(FailureMessageSanitizer.Sanitize(message, secret));
        Assert.DoesNotContain(secret, result);
        Assert.Equal(300, result.Length);
        Assert.EndsWith("…", result);
    }

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
        var escaped = JsonEncodedText.Encode(secret).ToString();
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

    private static string? SanitizeNaive(string? message, params string?[] secrets)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var forms = secrets.Where(secret => !string.IsNullOrEmpty(secret))
            .SelectMany(secret => new[]
            {
                secret!,
                JsonEncodedText.Encode(secret!).ToString(),
                JsonEncodedText.Encode(secret!, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
            })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length);
        // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; the Aggregate form reads worse for a rolling string rewrite.
        foreach (var form in forms)
            message = RedactSecretNaive(message, form);

        message = WhitespaceRun().Replace(message, " ").Trim();
        // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; the Aggregate form reads worse for a rolling string rewrite.
        foreach (var secret in secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)).OrderByDescending(secret => secret!.Length))
            message = RedactSecretNaive(message, WhitespaceRun().Replace(secret!, " ").Trim());

        return message.Length > 300 ? message[..299] + "…" : message;
    }

    private static string RedactSecretNaive(string message, string secret)
    {
        message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        const int windowLength = 24;
        if (secret.Length <= windowLength)
            return message;

        // Mark spans before replacing so overlapping windows cannot leave fragments behind.
        var redacted = new bool[message.Length];
        for (var offset = 0; offset <= secret.Length - windowLength; offset++)
        {
            var window = secret.Substring(offset, windowLength);
            for (var hit = message.IndexOf(window, StringComparison.Ordinal); hit >= 0;
                 hit = message.IndexOf(window, hit + 1, StringComparison.Ordinal))
            {
                var length = windowLength;
                while (hit + length < message.Length && offset + length < secret.Length
                       && message[hit + length] == secret[offset + length])
                    length++;
                Array.Fill(redacted, true, hit, length);
            }
        }

        var result = new System.Text.StringBuilder();
        for (var index = 0; index < message.Length; index++)
        {
            if (!redacted[index])
                result.Append(message[index]);
            else if (index == 0 || !redacted[index - 1])
                result.Append("[redacted]");
        }
        return result.ToString();
    }
}
