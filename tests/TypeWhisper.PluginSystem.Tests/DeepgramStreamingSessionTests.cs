using System.Net.WebSockets;
using System.Text;
using TypeWhisper.Plugin.Deepgram;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class DeepgramStreamingSessionTests
{
    [Theory]
    [InlineData("[\"de\"]", "de")]
    [InlineData("[\" de \"]", "de")]
    [InlineData("[\"de\",\"en\"]", null)]
    [InlineData(null, null)]
    [InlineData("[]", null)]
    [InlineData("[\" \"]", null)]
    [InlineData("[42]", null)]
    [InlineData("\"de\"", null)]
    public void Results_ReportsOnlyAnUnambiguousLanguage(string? languages, string? expected)
    {
        foreach (var isFinal in new[] { false, true })
        {
            var adapter = new DeepgramWebSocketAdapter("key", "nova-3", null);
            var languageProperty = languages is null ? "" : $",\"languages\":{languages}";
            var json = $$$"""
                {"type":"Results","is_final":{{{(isFinal ? "true" : "false")}}},
                 "channel":{"alternatives":[{"transcript":"Hallo Welt"{{{languageProperty}}}}]}}
                """;

            var result = adapter.HandleMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

            Assert.Null(result.Fault);
            var transcript = Assert.Single(result.Transcripts);
            Assert.Equal("Hallo Welt", transcript.Text);
            Assert.Equal(isFinal, transcript.IsFinal);
            Assert.Equal(expected, transcript.DetectedLanguage);
        }
    }
}
