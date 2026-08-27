using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

internal static class XaiTtsConfiguration
{
    internal const string DefaultVoiceId = "eve";
    internal const int SampleRate = 24_000;

    internal static IReadOnlyList<PluginVoiceInfo> FallbackVoices { get; } =
    [
        new("eve", "Eve"),
        new("ara", "Ara"),
        new("leo", "Leo"),
        new("rex", "Rex"),
        new("sal", "Sal"),
    ];

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string text,
        string? voice,
        string? language,
        bool lowLatency,
        bool textNormalization
    )
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceId : voice.Trim();
        var selectedLanguage = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();

        return new Dictionary<string, JsonElement>
        {
            ["text"] = XaiJson.Element(text),
            ["voice_id"] = XaiJson.Element(selectedVoice),
            ["language"] = XaiJson.Element(selectedLanguage),
            ["output_format"] = XaiJson.Element(new
            {
                codec = "pcm",
                sample_rate = SampleRate,
            }),
            ["optimize_streaming_latency"] = XaiJson.Element(lowLatency ? 1 : 0),
            ["text_normalization"] = XaiJson.Element(textNormalization),
        };
    }
}

internal sealed class XaiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static XaiInactiveTtsPlaybackSession Instance { get; } = new();

    private XaiInactiveTtsPlaybackSession()
    {
    }

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add => value?.Invoke(this, EventArgs.Empty);
        remove { }
    }

    public void Stop()
    {
    }
}
