namespace TypeWhisper.Linux.Services;

internal enum LinuxShortSpeechDecision
{
    DiscardTooShort,
    DiscardNoSpeech,
    Transcribe
}

internal static class LinuxDictationShortSpeechPolicy
{
    private const int WavHeaderBytes = 44;
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;
    private const double UltraShortTapSeconds = 0.04;
    private const double ShortClipSeconds = 1.0;
    private const double MinimumTranscriptionSeconds = 0.75;
    private const double TailPaddingSeconds = 0.3;
    private const float ShortClipQuietPeakThreshold = 0.003f;
    private const float LongClipQuietPeakThreshold = 0.006f;

    public static LinuxShortSpeechDecision Classify(
        double rawDuration,
        float peakLevel,
        bool transcribeShortQuietClipsAggressively = false)
    {
        if (rawDuration < UltraShortTapSeconds)
            return LinuxShortSpeechDecision.DiscardTooShort;

        if (rawDuration < ShortClipSeconds)
        {
            if (peakLevel < ShortClipQuietPeakThreshold)
            {
                return transcribeShortQuietClipsAggressively
                    ? LinuxShortSpeechDecision.Transcribe
                    : LinuxShortSpeechDecision.DiscardNoSpeech;
            }

            return LinuxShortSpeechDecision.Transcribe;
        }

        if (peakLevel < LongClipQuietPeakThreshold)
        {
            return transcribeShortQuietClipsAggressively
                ? LinuxShortSpeechDecision.Transcribe
                : LinuxShortSpeechDecision.DiscardNoSpeech;
        }

        return LinuxShortSpeechDecision.Transcribe;
    }

    public static byte[] PadWavForFinalTranscription(byte[] wav, double rawDuration)
    {
        if (!IsStandardPcm16MonoWav(wav))
            return wav;

        var currentSampleCount = (wav.Length - WavHeaderBytes) / BytesPerSample;
        var targetSampleCount = currentSampleCount;

        if (rawDuration < MinimumTranscriptionSeconds)
            targetSampleCount = Math.Max(targetSampleCount, (int)(MinimumTranscriptionSeconds * SampleRate));
        else
            targetSampleCount += (int)(TailPaddingSeconds * SampleRate);

        if (targetSampleCount <= currentSampleCount)
            return wav;

        var padded = new byte[WavHeaderBytes + targetSampleCount * BytesPerSample];
        Array.Copy(wav, padded, wav.Length);
        WriteInt32LittleEndian(padded, 4, 36 + targetSampleCount * BytesPerSample);
        WriteInt32LittleEndian(padded, 40, targetSampleCount * BytesPerSample);
        return padded;
    }

    public static double ComputeDurationSeconds(byte[] wav)
    {
        if (!IsStandardPcm16MonoWav(wav))
            return 0;

        return (wav.Length - WavHeaderBytes) / (double)(SampleRate * BytesPerSample);
    }

    public static float ComputePeakLevel(byte[] wav)
    {
        if (!IsStandardPcm16MonoWav(wav))
            return 0f;

        var peak = 0;
        for (var i = WavHeaderBytes; i + 1 < wav.Length; i += BytesPerSample)
        {
            var sample = BitConverter.ToInt16(wav, i);
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        return peak / (float)short.MaxValue;
    }

    private static bool IsStandardPcm16MonoWav(byte[] wav) =>
        wav.Length >= WavHeaderBytes &&
        wav[0] == (byte)'R' &&
        wav[1] == (byte)'I' &&
        wav[2] == (byte)'F' &&
        wav[3] == (byte)'F' &&
        wav[8] == (byte)'W' &&
        wav[9] == (byte)'A' &&
        wav[10] == (byte)'V' &&
        wav[11] == (byte)'E' &&
        BitConverter.ToInt16(wav, 20) == 1 &&
        BitConverter.ToInt16(wav, 22) == 1 &&
        BitConverter.ToInt32(wav, 24) == SampleRate &&
        BitConverter.ToInt16(wav, 34) == 16;

    private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, buffer, offset, bytes.Length);
    }
}
