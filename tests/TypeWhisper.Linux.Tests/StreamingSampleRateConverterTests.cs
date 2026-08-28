using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class StreamingSampleRateConverterTests
{
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Append_TwoFrames_EqualsSingleBatchConversion(int sourceSampleRate)
    {
        var input = CreateSignal(1024, sourceSampleRate);
        var converter = new StreamingSampleRateConverter(16000);
        var actual = converter.Append(input[..512], sourceSampleRate)
            .Concat(converter.Append(input[512..], sourceSampleRate))
            .Concat(converter.Complete())
            .ToArray();

        var expected = AudioRecordingService.ResampleToSampleRate(
            input,
            sourceSampleRate,
            16000
        );

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Append_ManySmallFrames_EqualsBlockConversion(int sourceSampleRate)
    {
        // Twenty 512-sample frames force repeated history trimming with a large
        // _historyStartIndex, and the batch side now feeds 8192-sample blocks, so the
        // two sides genuinely exercise different framings of the same converter.
        var input = CreateSignal(20 * 512, sourceSampleRate);
        var converter = new StreamingSampleRateConverter(16000);
        var actual = new List<float>();
        for (var offset = 0; offset < input.Length; offset += 512)
        {
            actual.AddRange(converter.Append(input[offset..(offset + 512)], sourceSampleRate));
        }

        actual.AddRange(converter.Complete());

        var expected = AudioRecordingService.ResampleToSampleRate(
            input,
            sourceSampleRate,
            16000
        );

        Assert.Equal(expected, actual.ToArray());
    }

    [Fact]
    public void Complete_EmitsExactBatchTail_OnlyOnce()
    {
        // NOTE: with a 777-sample input the batch reference below runs the identical
        // single Append+Complete sequence, so the two Assert.Equal lines cannot fail on
        // their own — the information here is the non-degenerate prefix/tail split and
        // the double-Complete idempotence check. Cross-framing equivalence is proven by
        // the two-frame and many-small-frames tests.
        const int sourceSampleRate = 48000;
        var input = CreateSignal(777, sourceSampleRate);
        var converter = new StreamingSampleRateConverter(16000);

        var prefix = converter.Append(input, sourceSampleRate);
        var tail = converter.Complete();
        var expected = AudioRecordingService.ResampleToSampleRate(
            input,
            sourceSampleRate,
            16000
        );

        Assert.NotEmpty(prefix);
        Assert.NotEmpty(tail);
        Assert.Equal(expected[..prefix.Length], prefix);
        Assert.Equal(expected[prefix.Length..], tail);
        Assert.Empty(converter.Complete());
    }

    [Fact]
    public void Append_RateChange_EqualsTwoIndependentBatchSegments()
    {
        const int firstSampleRate = 48000;
        const int secondSampleRate = 44100;
        var first = CreateSignal(683, firstSampleRate);
        var second = CreateSignal(577, secondSampleRate, sampleOffset: first.Length);
        var converter = new StreamingSampleRateConverter(16000);

        var actual = converter.Append(first, firstSampleRate)
            .Concat(converter.Append(second, secondSampleRate))
            .Concat(converter.Complete())
            .ToArray();
        var expected = AudioRecordingService.ResampleToSampleRate(
                first,
                firstSampleRate,
                16000
            )
            .Concat(AudioRecordingService.ResampleToSampleRate(
                second,
                secondSampleRate,
                16000
            ))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static float[] CreateSignal(int sampleCount, int sampleRate, int sampleOffset = 0)
    {
        var samples = new float[sampleCount];
        for (var i = 0; i < samples.Length; i++)
        {
            var sampleIndex = i + sampleOffset;
            samples[i] = (float)(
                0.55 * Math.Sin(2 * Math.PI * 997 * sampleIndex / sampleRate)
                + 0.25 * Math.Sin(2 * Math.PI * 5107 * sampleIndex / sampleRate)
                + 0.0001 * (sampleIndex % 29)
            );
        }

        return samples;
    }
}
