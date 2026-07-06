using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="SettingsService" />: save/load round-trips, atomic-write/backup recovery, and legacy-field migrations.</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly string _tempDir;

    public SettingsServiceTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), $"tw_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Join(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.Default.Language, sut.Current.Language);
        Assert.False(sut.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var sut = new SettingsService(_filePath);
        var settings = AppSettings.Default with
        {
            Language = "de",
            HasCompletedOnboarding = true,
            VocabularyBoostingEnabled = true,
            AutoAddDictionaryCorrections = true,
            CleanupLevel = CleanupLevel.Light,
            PreviewBubbleAutoHideMilliseconds = 3750,
            OverlayCustomLeft = 123.5,
            OverlayCustomTop = 87.25,
            SelectedIndustryPresetId = "real-estate",
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda,
            LiveTranscriptionStreamingEnabled = true,
            AppInsertionStrategies = new Dictionary<string, TextInsertionStrategy>
            {
                ["kitty"] = TextInsertionStrategy.DirectTyping,
                ["firefox"] = TextInsertionStrategy.ClipboardPaste
            }
        };

        sut.Save(settings);

        var sut2 = new SettingsService(_filePath);
        Assert.Equal("de", sut2.Current.Language);
        Assert.True(sut2.Current.HasCompletedOnboarding);
        Assert.True(sut2.Current.VocabularyBoostingEnabled);
        Assert.True(sut2.Current.AutoAddDictionaryCorrections);
        Assert.Equal(CleanupLevel.Light, sut2.Current.CleanupLevel);
        Assert.Equal(3750, sut2.Current.PreviewBubbleAutoHideMilliseconds);
        Assert.Equal(123.5, sut2.Current.OverlayCustomLeft);
        Assert.Equal(87.25, sut2.Current.OverlayCustomTop);
        Assert.Equal("real-estate", sut2.Current.SelectedIndustryPresetId);
        Assert.Equal(
            AppSettings.LocalModelAccelerationNvidiaCuda,
            sut2.Current.LocalModelAcceleration
        );
        Assert.True(sut2.Current.LiveTranscriptionStreamingEnabled);
        Assert.Equal(
            TextInsertionStrategy.DirectTyping,
            sut2.Current.AppInsertionStrategies["kitty"]
        );
        Assert.Equal(
            TextInsertionStrategy.ClipboardPaste,
            sut2.Current.AppInsertionStrategies["firefox"]
        );
    }

    [Fact]
    public void Save_CreatesBackupFile()
    {
        var sut = new SettingsService(_filePath);
        var first = AppSettings.Default with { Language = "en" };
        sut.Save(first);

        var second = AppSettings.Default with { Language = "fr" };
        sut.Save(second);

        var bakPath = _filePath + ".bak";
        Assert.True(File.Exists(bakPath));

        var bakJson = File.ReadAllText(bakPath);
        Assert.Contains("en", bakJson);
    }

    [Fact]
    public void Save_DoesNotLeaveTemp()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default with { Language = "de" });

        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void Load_CorruptPrimary_FallsBackToBackup()
    {
        var backup = AppSettings.Default with { Language = "de", HasCompletedOnboarding = true };
        var json = JsonSerializer.Serialize(backup, s_jsonOptions);
        File.WriteAllText(_filePath + ".bak", json);
        File.WriteAllText(_filePath, "{{not valid json!!");

        var sut = new SettingsService(_filePath);

        Assert.Equal("de", sut.Current.Language);
        Assert.True(sut.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void Load_CorruptPrimaryAndBackup_ReturnsDefaults()
    {
        File.WriteAllText(_filePath, "{{corrupt}}");
        File.WriteAllText(_filePath + ".bak", "{{also corrupt}}");

        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.Default.Language, sut.Current.Language);
    }

    [Fact]
    public void Load_CorruptPrimary_RestoresPrimaryFromBackup()
    {
        var backup = AppSettings.Default with { Language = "de" };
        var json = JsonSerializer.Serialize(backup, s_jsonOptions);
        File.WriteAllText(_filePath + ".bak", json);
        File.WriteAllText(_filePath, "{{corrupt}}");

        _ = new SettingsService(_filePath);

        // Primary should now be restored from backup
        var primaryJson = File.ReadAllText(_filePath);
        Assert.Contains("de", primaryJson);
    }

    [Fact]
    public void Save_FiresSettingsChangedEvent()
    {
        var sut = new SettingsService(_filePath);
        AppSettings? received = null;
        sut.SettingsChanged += s => received = s;

        var settings = AppSettings.Default with { Language = "es" };
        sut.Save(settings);

        Assert.NotNull(received);
        Assert.Equal("es", received.Language);
    }

    [Fact]
    public void Load_LegacyHistoryRetentionDays_MigratesToMinutes()
    {
        File.WriteAllText(
            _filePath,
            """
            {
              "language": "en",
              "historyRetentionDays": 7
            }
            """
        );

        var sut = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.Duration, sut.Current.HistoryRetentionMode);
        Assert.Equal(7 * 24 * 60, sut.Current.HistoryRetentionMinutes);
    }

    [Fact]
    public void Load_LegacyForeverRetention_MigratesToExplicitMode()
    {
        File.WriteAllText(
            _filePath,
            """
            {
              "language": "en",
              "historyRetentionDays": 9999
            }
            """
        );

        var sut = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.Forever, sut.Current.HistoryRetentionMode);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsMinuteBasedRetention()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(
            AppSettings.Default with
            {
                HistoryRetentionMode = HistoryRetentionMode.Duration,
                HistoryRetentionMinutes = 60
            }
        );

        var loaded = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.Duration, loaded.Current.HistoryRetentionMode);
        Assert.Equal(60, loaded.Current.HistoryRetentionMinutes);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsUntilAppClosesMode()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(
            AppSettings.Default with
            {
                HistoryRetentionMode = HistoryRetentionMode.UntilAppCloses
            }
        );

        var loaded = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.UntilAppCloses, loaded.Current.HistoryRetentionMode);
    }

    [Fact]
    public void Load_LegacyComputeBackendCuda_MigratesToNvidiaCuda()
    {
        File.WriteAllText(
            _filePath,
            """
            {
              "language": "en",
              "computeBackend": "cuda"
            }
            """
        );

        var sut = new SettingsService(_filePath);

        Assert.Equal(
            AppSettings.LocalModelAccelerationNvidiaCuda,
            sut.Current.LocalModelAcceleration);
    }

    [Fact]
    public void Load_LegacyComputeBackendCpu_MigratesToCpu()
    {
        File.WriteAllText(
            _filePath,
            """
            {
              "language": "en",
              "computeBackend": "cpu"
            }
            """
        );

        var sut = new SettingsService(_filePath);

        Assert.Equal(
            AppSettings.LocalModelAccelerationCpu,
            sut.Current.LocalModelAcceleration);
    }

    [Fact]
    public void Load_LegacyComputeBackendUnset_DefaultsToCpu()
    {
        // Older fork builds defaulted ComputeBackend to "cpu". When the legacy
        // field is present but empty/missing-value, preserve that default by
        // mapping to LocalModelAccelerationCpu rather than Auto.
        File.WriteAllText(
            _filePath,
            """
            {
              "language": "en",
              "computeBackend": ""
            }
            """
        );

        var sut = new SettingsService(_filePath);

        Assert.Equal(
            AppSettings.LocalModelAccelerationCpu,
            sut.Current.LocalModelAcceleration);
    }

    [Fact]
    public void Load_UnknownLocalModelAcceleration_FallsBackToAuto()
    {
        File.WriteAllText(
            _filePath,
            """
            {
              "language": "en",
              "localModelAcceleration": "directml"
            }
            """
        );

        var sut = new SettingsService(_filePath);

        Assert.Equal(
            AppSettings.LocalModelAccelerationAuto,
            sut.Current.LocalModelAcceleration);
    }

    [Fact]
    public void Load_BothLegacyAndNewFields_PrefersNewField()
    {
        // Migration only runs when localModelAcceleration is absent. When both
        // exist, the new field wins — guarantees a one-shot migration that
        // doesn't keep overwriting an explicit user choice.
        File.WriteAllText(
            _filePath,
            """
            {
              "language": "en",
              "computeBackend": "cuda",
              "localModelAcceleration": "cpu"
            }
            """
        );

        var sut = new SettingsService(_filePath);

        Assert.Equal(
            AppSettings.LocalModelAccelerationCpu,
            sut.Current.LocalModelAcceleration);
    }
}
