using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public sealed class LlmUsageSummaryTests
{
    [Fact]
    public void From_NoCalls_IsEmpty()
    {
        Assert.Equal(default, LlmUsageSummary.From([]));
    }

    [Fact]
    public void From_OneModel_SumsAnsweredCalls()
    {
        var summary = LlmUsageSummary.From([
            Call("Cleanup", "OpenAI", "gpt-4o", 0.5),
            Call("Translation", "OpenAI", "gpt-4o", 1.25),
        ]);

        Assert.Equal(new LlmUsageSummary("OpenAI", "gpt-4o", 1.75), summary);
    }

    [Fact]
    public void From_SeveralModels_ListsEachPairAndLeavesModelNull()
    {
        var summary = LlmUsageSummary.From([
            Call("Cleanup", "OpenAI", "gpt-4o", 0.5),
            Call("PromptAction", "Groq", "llama", 1),
        ]);

        Assert.Equal(new LlmUsageSummary("OpenAI · gpt-4o, Groq · llama", null, 1.5), summary);
    }

    [Fact]
    public void From_OnlyFailedCalls_KeepsTheLlmButNoTime()
    {
        var summary = LlmUsageSummary.From([Call("PromptAction", "OpenAI", "gpt-4o", null)]);

        Assert.Equal(new LlmUsageSummary("OpenAI", "gpt-4o", null), summary);
    }

    [Fact]
    public void From_IgnoresMemoryExtraction()
    {
        Assert.Equal(default, LlmUsageSummary.From([Call("Memory", "OpenAI", "gpt-4o", 2)]));
    }

    [Theory]
    [InlineData("reply", 1.5)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Complete_RecordsDurationOnlyForANonEmptyReply(string? response, double? expected)
    {
        var call = Call("Cleanup", "OpenAI", "gpt-4o", null);

        call.Complete(response, TimeSpan.FromSeconds(1.5));

        Assert.Equal(response, call.ResponseReceived);
        Assert.Equal(expected, call.DurationSeconds);
    }

    [Fact]
    public void Record_OmitsMissingProcessingDetailsFromJson_AndRoundTripsThem()
    {
        var record = new TranscriptionRecord
        {
            Id = "id", Timestamp = DateTime.UnixEpoch, RawText = "raw", FinalText = "final",
        };
        var json = JsonSerializer.Serialize(record);
        Assert.DoesNotContain(nameof(TranscriptionRecord.TranscriptionSeconds), json);
        Assert.DoesNotContain(nameof(TranscriptionRecord.LlmProvider), json);

        var detailed = record with
        {
            TranscriptionSeconds = 1.2, LlmProvider = "OpenAI", LlmModel = "gpt-4o", LlmSeconds = 0.8,
        };
        var back = JsonSerializer.Deserialize<TranscriptionRecord>(JsonSerializer.Serialize(detailed))!;
        Assert.Equal(1.2, back.TranscriptionSeconds);
        Assert.Equal("OpenAI", back.LlmProvider);
        Assert.Equal("gpt-4o", back.LlmModel);
        Assert.Equal(0.8, back.LlmSeconds);
    }

    private static LlmCallProvenance Call(string stage, string provider, string model, double? seconds) => new()
    {
        Stage = stage,
        SystemPromptSent = "",
        UserPromptSent = "",
        ProviderName = provider,
        ModelId = model,
        DurationSeconds = seconds,
    };
}
