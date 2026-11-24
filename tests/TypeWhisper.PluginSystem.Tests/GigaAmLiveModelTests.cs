extern alias GigaAm;
using GigaAm::TypeWhisper.Plugin.GigaAm;
using Xunit.Abstractions;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Runs the real GigaAM model when <c>GIGAAM_MODEL_DIR</c> names a directory with the four
///     downloaded files and <c>GIGAAM_AUDIO</c> a 16 kHz mono PCM16 WAV of Russian speech.
/// </summary>
public sealed class GigaAmLiveModelTests(ITestOutputHelper output)
{
    [GigaAmLiveFact]
    public void TranscribesRealAudio()
    {
        var samples = GigaAmPlugin.DecodeWav(
            File.ReadAllBytes(Environment.GetEnvironmentVariable("GIGAAM_AUDIO")!)
        );
        using var runtime = new GigaAmRuntime(
            Environment.GetEnvironmentVariable("GIGAAM_MODEL_DIR")!,
            GigaAmPlugin.PreprocessorPath
        );

        var result = GigaAmPlugin.Transcribe(runtime, samples, CancellationToken.None);
        output.WriteLine(result.Text);

        Assert.Contains(result.Text, character => character is >= 'а' and <= 'я');
        Assert.Equal("ru", result.DetectedLanguage);
    }

    private sealed class GigaAmLiveFactAttribute : FactAttribute
    {
        public GigaAmLiveFactAttribute()
        {
            if (
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIGAAM_MODEL_DIR"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIGAAM_AUDIO"))
            )
            {
                Skip = "Set GIGAAM_MODEL_DIR and GIGAAM_AUDIO to run the real GigaAM model.";
            }
        }
    }
}
