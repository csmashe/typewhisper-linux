using System.Net.WebSockets;
using System.Text;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SonioxStreamingSessionTests
{
    [Fact]
    public void ParseMessage_PreservesTokenLanguages()
    {
        var message = SonioxStreamingSession.ParseMessage(
            """{"tokens":[{"text":"Hallo","is_final":true,"language":"de"},{"text":" Welt","is_final":false}]}""");

        Assert.Equal("de", message.Tokens[0].Language);
        Assert.Null(message.Tokens[1].Language);
    }

    [Theory]
    [InlineData("\"de\"", "\"de\"", "de")]
    [InlineData("\"de\"", "\"DE\"", "de")]
    [InlineData("\"de\"", "\"en\"", null)]
    [InlineData("null", "null", null)]
    [InlineData("\" \"", "null", null)]
    public void Finished_UsesLanguagesFromFinalTokensAcrossMessages(
        string firstLanguage, string secondLanguage, string? expected)
    {
        var adapter = CreateAdapter();
        Handle(adapter, $$"""
            {"tokens":[{"text":"Hallo","is_final":true,"language":{{firstLanguage}}},
                       {"text":" provisional","is_final":false,"language":"fr"}]}
            """);
        var result = Handle(adapter, $$"""
            {"tokens":[{"text":" Welt","is_final":true,"language":{{secondLanguage}}}],"finished":true}
            """);

        var transcript = Assert.Single(result.Transcripts);
        Assert.True(transcript.IsFinal);
        Assert.Equal("Hallo Welt", transcript.Text);
        Assert.Equal(expected, transcript.DetectedLanguage);
    }

    [Fact]
    public void Finished_WithoutTokenLanguages_ReportsNoLanguage()
    {
        var result = Handle(CreateAdapter(),
            """{"tokens":[{"text":"Hallo","is_final":true}],"finished":true}""");

        Assert.Null(Assert.Single(result.Transcripts).DetectedLanguage);
    }

    [Theory]
    [InlineData("de", "de", "de")]
    [InlineData("de", "DE", "de")]
    [InlineData("de", "en", null)]
    [InlineData("de", " ", "de")]
    [InlineData("", " ", null)]
    public void Preview_UsesOnlyLanguagesInCurrentMessage(string first, string second, string? expected)
    {
        var adapter = CreateAdapter();
        Handle(adapter, """{"tokens":[{"text":"Vorher. ","is_final":true,"language":"fr"}]}""");
        var result = Handle(adapter, $$"""
            {"tokens":[{"text":"Hallo","is_final":true,"language":"{{first}}"},
                       {"text":" Welt","is_final":false,"language":"{{second}}"}]}
            """);

        var transcript = Assert.Single(result.Transcripts);
        Assert.False(transcript.IsFinal);
        Assert.Equal("Vorher. Hallo Welt", transcript.Text);
        Assert.Equal(expected, transcript.DetectedLanguage);
    }

    private static SonioxWebSocketAdapter CreateAdapter() =>
        new("key", new Uri("wss://example.com/realtime"), []);

    private static WebSocketInboundResult Handle(SonioxWebSocketAdapter adapter, string json) =>
        adapter.HandleMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));
}
