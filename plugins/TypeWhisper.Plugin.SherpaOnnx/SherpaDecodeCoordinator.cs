using System.Text.Json;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal delegate string SherpaDecodeDelegate(float[] audioSamples);

internal readonly record struct SherpaDecodeResult(string Text, string? DetectedLanguage);

internal sealed class SherpaDecodeCoordinator
{
    internal const int SampleRate = 16000;
    internal const int MaximumChunkDurationSeconds = 15;
    internal const int MaximumChunkSampleCount = SampleRate * MaximumChunkDurationSeconds;

    private const int BoundarySearchDurationSeconds = 2;
    private const int BoundarySearchSampleCount = SampleRate * BoundarySearchDurationSeconds;
    private const int EnergyWindowMilliseconds = 20;
    private const int EnergyWindowSampleCount = SampleRate * EnergyWindowMilliseconds / 1000;
    private const int EnergySearchStrideMilliseconds = 10;
    private const int EnergySearchStrideSampleCount =
        SampleRate * EnergySearchStrideMilliseconds / 1000;
    private const int OverlapDurationMilliseconds = 500;
    private const int OverlapSampleCount = SampleRate * OverlapDurationMilliseconds / 1000;

    private readonly SherpaDecodeDelegate _decode;

    internal SherpaDecodeCoordinator(SherpaDecodeDelegate decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        _decode = decode;
    }

    internal SherpaDecodeResult Decode(
        float[] audioSamples,
        bool parseCanaryPayload,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(audioSamples);
        ct.ThrowIfCancellationRequested();

        string? stitchedText = null;
        string? detectedLanguage = null;
        foreach (var chunk in CreateChunks(audioSamples, ct))
        {
            // sherpa-onnx 1.12.23 exposes only a synchronous Decode call. These
            // checkpoints cannot interrupt that call, but chunking bounds normal
            // uncancellable work and stops before the next native invocation.
            ct.ThrowIfCancellationRequested();
            var rawText = _decode(chunk);
            ct.ThrowIfCancellationRequested();

            var result = parseCanaryPayload
                ? ParseCanaryResult(rawText)
                : new SherpaDecodeResult(rawText.Trim(), null);
            stitchedText = stitchedText is null
                ? result.Text
                : StitchTokenOverlap(stitchedText, result.Text);
            detectedLanguage ??= result.DetectedLanguage;
        }

        // Do not publish a completed aggregate after cancellation raced the final
        // chunk's parsing/stitching work.
        ct.ThrowIfCancellationRequested();
        return new SherpaDecodeResult(stitchedText ?? string.Empty, detectedLanguage);
    }

    private static IEnumerable<float[]> CreateChunks(
        float[] audioSamples,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        // Preserve the existing single-call path for short recordings, including an
        // empty recording. Only long audio pays the copy/overlap cost.
        if (audioSamples.Length <= MaximumChunkSampleCount)
        {
            yield return audioSamples;
            yield break;
        }

        var start = 0;
        while (audioSamples.Length - start > MaximumChunkSampleCount)
        {
            ct.ThrowIfCancellationRequested();
            var hardEnd = start + MaximumChunkSampleCount;
            var cut = FindLowEnergyCut(audioSamples, start, hardEnd);
            yield return audioSamples.AsSpan(start, cut - start).ToArray();
            start = cut - OverlapSampleCount;
        }

        ct.ThrowIfCancellationRequested();
        yield return audioSamples.AsSpan(start).ToArray();
    }

    private static int FindLowEnergyCut(float[] audioSamples, int start, int hardEnd)
    {
        var searchStart = Math.Max(
            start + OverlapSampleCount + EnergyWindowSampleCount,
            hardEnd - BoundarySearchSampleCount
        );
        var halfWindow = EnergyWindowSampleCount / 2;
        var bestCut = hardEnd;
        var bestEnergy = double.MaxValue;

        for (
            var candidate = searchStart;
            candidate <= hardEnd;
            candidate += EnergySearchStrideSampleCount
        )
        {
            var windowStart = Math.Max(start, candidate - halfWindow);
            var windowEnd = Math.Min(audioSamples.Length, candidate + halfWindow);
            double energy = 0;
            for (var i = windowStart; i < windowEnd; i++)
                energy += audioSamples[i] * audioSamples[i];

            energy /= Math.Max(1, windowEnd - windowStart);
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestCut = candidate;
            }
        }

        return bestCut;
    }

    private static string StitchTokenOverlap(string accumulated, string next)
    {
        if (string.IsNullOrWhiteSpace(accumulated))
            return next.Trim();
        if (string.IsNullOrWhiteSpace(next))
            return accumulated.Trim();

        var accumulatedTokens = SplitTokens(accumulated);
        var nextTokens = SplitTokens(next);
        var maximumOverlap = Math.Min(accumulatedTokens.Length, nextTokens.Length);
        var overlap = 0;

        for (var length = maximumOverlap; length > 0; length--)
        {
            var matches = true;
            for (var i = 0; i < length; i++)
            {
                if (
                    !string.Equals(
                        accumulatedTokens[accumulatedTokens.Length - length + i],
                        nextTokens[i],
                        StringComparison.Ordinal
                    )
                )
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                overlap = length;
                break;
            }
        }

        return string.Join(' ', accumulatedTokens.Concat(nextTokens.Skip(overlap)));
    }

    private static string[] SplitTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static SherpaDecodeResult ParseCanaryResult(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new SherpaDecodeResult(string.Empty, null);

        try
        {
            using var json = JsonDocument.Parse(rawText);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return new SherpaDecodeResult(rawText.Trim(), null);

            var text = rawText.Trim();
            if (json.RootElement.TryGetProperty("text", out var textNode))
                text = textNode.GetString()?.Trim() ?? string.Empty;

            string? language = null;
            if (json.RootElement.TryGetProperty("lang", out var languageNode))
            {
                var parsed = languageNode.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                    language = parsed;
            }

            return new SherpaDecodeResult(text, language);
        }
        catch (JsonException)
        {
            return new SherpaDecodeResult(rawText.Trim(), null);
        }
    }
}
