using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

internal static class OpenAiTtsConfiguration
{
    private const string ModelId = "gpt-4o-mini-tts";
    internal const string DefaultVoiceId = "marin";
    internal const int SampleRate = 24_000;

    internal static IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; } =
    [
        new("alloy", "Alloy"),
        new("ash", "Ash"),
        new("ballad", "Ballad"),
        new("coral", "Coral"),
        new("echo", "Echo"),
        new("fable", "Fable"),
        new("nova", "Nova"),
        new("onyx", "Onyx"),
        new("sage", "Sage"),
        new("shimmer", "Shimmer"),
        new("verse", "Verse"),
        new("marin", "Marin"),
        new("cedar", "Cedar"),
    ];

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string text,
        string? voice,
        string? instructions
    )
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceId : voice;
        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = OpenAiJson.Element(ModelId),
            ["input"] = OpenAiJson.Element(text),
            ["voice"] = OpenAiJson.Element(selectedVoice),
            ["response_format"] = OpenAiJson.Element("pcm"),
        };

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            body["instructions"] = OpenAiJson.Element(instructions.Trim());
        }

        return body;
    }
}

internal sealed class OpenAiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static OpenAiInactiveTtsPlaybackSession Instance { get; } = new();

    private OpenAiInactiveTtsPlaybackSession()
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
