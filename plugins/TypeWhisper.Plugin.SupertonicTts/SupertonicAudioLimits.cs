namespace TypeWhisper.Plugin.SupertonicTts;

/// <summary>Bounds generated speech to two minutes so a runaway duration prediction cannot exhaust memory.</summary>
internal static class SupertonicAudioLimits
{
    private const int MaxDurationSeconds = 120;

    internal static long MaximumSamples(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new InvalidOperationException("The generated speech has an invalid sample rate.");
        return (long)sampleRate * MaxDurationSeconds;
    }

    internal static void ValidateSampleCount(double sampleCount, long maximumSamples)
    {
        if (!double.IsFinite(sampleCount) || sampleCount < 0 || sampleCount > maximumSamples)
            throw new InvalidOperationException("The generated speech exceeds the two-minute audio limit.");
    }
}
