using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class DictationRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), $"tw_recovery_{Guid.NewGuid():N}");

    public DictationRecoveryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(TranscriptionRecordStatus.Succeeded, 0)]
    [InlineData(TranscriptionRecordStatus.TranscriptionFailed, 1)]
    [InlineData(TranscriptionRecordStatus.ProcessingFailed, 2)]
    public void Status_RoundTripsWithStableOrdinals(TranscriptionRecordStatus status, int ordinal)
    {
        var record = Record() with { Status = status, TranscriptionTaskUsed = "translate" };
        var json = JsonSerializer.Serialize(record);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(ordinal, document.RootElement.GetProperty("Status").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("WordCount", out _));
        Assert.False(document.RootElement.TryGetProperty("Preview", out _));
        var restored = JsonSerializer.Deserialize<TranscriptionRecord>(json)!;
        Assert.Equal(status, restored.Status);
        Assert.Equal("translate", restored.TranscriptionTaskUsed);
        Assert.Equal(TranscriptionRecordStatus.Succeeded,
            JsonSerializer.Deserialize<TranscriptionRecord>("""{"Id":"old","Timestamp":"2026-01-01","RawText":"old","FinalText":"old"}""")!.Status);
    }

    [Fact]
    public void Replace_PreservesAudioAndIdentity_AndDoesNotAddMissingRecord()
    {
        var path = Path.Join(_directory, "history.json");
        var history = new HistoryService(path, _directory);
        var record = Record() with { AudioFileName = "dictation-test.wav" };
        File.WriteAllText(Path.Join(_directory, record.AudioFileName), "audio");
        history.AddRecord(record);
        var changed = 0;
        history.RecordsChanged += () => changed++;
        Assert.True(history.TryReplaceRecord(record with { FinalText = "recovered" }));
        Assert.False(history.TryReplaceRecord(record with { Id = "missing" }));
        Assert.Equal(1, changed);
        var restored = Assert.Single(new HistoryService(path).Records);
        Assert.Equal(record.Id, restored.Id);
        Assert.Equal("recovered", restored.FinalText);
        Assert.Equal(record.AudioFileName, restored.AudioFileName);
        Assert.True(File.Exists(Path.Join(_directory, record.AudioFileName)));
    }

    [Fact]
    public void Insights_ExcludeFailuresFromAllTotals()
    {
        var success = Record();
        var failed = success with { Status = TranscriptionRecordStatus.ProcessingFailed, FinalText = "many failed words", DurationSeconds = 200 };
        var insights = new HistoryInsightsService().Build([success, failed,
            failed with { Status = TranscriptionRecordStatus.TranscriptionFailed }]);
        Assert.Equal(1, insights.TotalRecords);
        Assert.Equal(success.WordCount, insights.TotalWords);
        Assert.Equal(success.DurationSeconds, insights.AverageDurationSeconds);
        Assert.Equal(1, Assert.Single(insights.TopApps).RecordCount);
        var history = new HistoryService(Path.Join(_directory, "history.json"));
        history.AddRecord(success);
        history.AddRecord(failed with { Id = "failed" });
        Assert.Equal(1, history.TotalRecords);
        Assert.Equal(success.WordCount, history.TotalWords);
        Assert.Equal(success.DurationSeconds, history.TotalDuration);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(30, 30)]
    [InlineData(900, 365)]
    public void Retention_ClampsSaveLoadAndUpdate(int input, int expected)
    {
        var path = Path.Join(_directory, "settings.json");
        var settings = new SettingsService(path);
        Assert.Equal(30, settings.Current.DictationRecoveryRetentionDays);
        settings.Save(AppSettings.Default with { DictationRecoveryRetentionDays = input });
        Assert.Equal(expected, settings.Current.DictationRecoveryRetentionDays);
        Assert.Equal(expected, new SettingsService(path).Current.DictationRecoveryRetentionDays);
        settings.Update(current => current with { DictationRecoveryRetentionDays = input, Language = "de" });
        Assert.Equal(expected, settings.Current.DictationRecoveryRetentionDays);
        File.WriteAllText(path, JsonSerializer.Serialize(new { dictationRecoveryRetentionDays = input }));
        Assert.Equal(expected, new SettingsService(path).Current.DictationRecoveryRetentionDays);
    }

    private static TranscriptionRecord Record() => new()
    {
        Id = "capture", Timestamp = DateTime.UtcNow, RawText = "hello", FinalText = "hello",
        DurationSeconds = 2, AppProcessName = "test",
    };
}
