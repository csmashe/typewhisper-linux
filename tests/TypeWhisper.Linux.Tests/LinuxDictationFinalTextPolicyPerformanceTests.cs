using System.Diagnostics;
using System.Text;
using TypeWhisper.Linux.Services;
using Xunit;
using Xunit.Abstractions;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxDictationFinalTextPolicyPerformanceTests(ITestOutputHelper output)
{
    // ~45x headroom over the current algorithm (~223 ms at 10k words, Debug) while the old
    // cubic one took 146 s at 8k words — loose enough to never flake, tight enough to catch
    // a complexity regression. Don't tighten toward the measured time.
    private static readonly TimeSpan s_maximumProcessingTime = TimeSpan.FromMilliseconds(10_000);

    [Theory]
    [InlineData(500)]
    [InlineData(1_000)]
    [InlineData(2_000)]
    [InlineData(4_000)]
    [InlineData(8_000)]
    [InlineData(10_000)]
    public void SelectRawText_LongNonRepeatingDictationCompletesWithinBound(int wordCount)
    {
        var rawText = BuildUniqueTokenText(wordCount);

        // Warm-up: keep one-time JIT and generated-regex init out of the measurement.
        LinuxDictationFinalTextPolicy.SelectRawText("one two three four five six");

        var stopwatch = Stopwatch.StartNew();
        var result = LinuxDictationFinalTextPolicy.SelectRawText(rawText);
        stopwatch.Stop();

        output.WriteLine("{0:N0} words: {1:F3} ms", wordCount, stopwatch.Elapsed.TotalMilliseconds);
        Assert.Equal(rawText, result);
        Assert.True(
            stopwatch.Elapsed < s_maximumProcessingTime,
            $"Processing {wordCount:N0} unique words took {stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
            $"expected less than {s_maximumProcessingTime.TotalMilliseconds:F0} ms.");
    }

    private static string BuildUniqueTokenText(int wordCount)
    {
        var builder = new StringBuilder(wordCount * 16);
        for (var i = 0; i < wordCount; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append("uniqueword");
            builder.Append(i);
        }

        return builder.ToString();
    }
}
