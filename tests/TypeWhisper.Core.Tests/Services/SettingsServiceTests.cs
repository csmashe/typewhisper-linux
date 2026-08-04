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

    /// <summary>
    ///     Runs <paramref name="body" /> on <paramref name="participantCount" /> dedicated threads that
    ///     all start together. Dedicated rather than pooled: a <see cref="Barrier" /> needs every
    ///     participant running at once, and the thread pool grows past <c>ProcessorCount</c> only slowly.
    /// </summary>
    private static void RunConcurrently(int participantCount, Action<int> body)
    {
        using var barrier = new Barrier(participantCount);
        var failures = new List<Exception>();
        var threads = new Thread[participantCount];
        for (var i = 0; i < participantCount; i++)
        {
            var idx = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    // ReSharper disable once AccessToDisposedClosure -- disposed only after every thread is joined below.
                    barrier.SignalAndWait();
                    body(idx);
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            }) { IsBackground = true };
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

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
    public void Save_WhileASubscriberIsStillDelivering_SerializesPublicationInCommitOrder()
    {
        var sut = new SettingsService(_filePath);
        var firstDelivering = new ManualResetEventSlim();
        var releaseFirst = new ManualResetEventSlim();
        var received = new List<string?>();
        var blockFirstDelivery = true;

        sut.SettingsChanged += s =>
        {
            lock (received)
            {
                received.Add(s.Language);
            }

            if (!blockFirstDelivery)
            {
                return;
            }

            blockFirstDelivery = false;
            firstDelivering.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(5));
        };

        var writerA = new Thread(() => sut.Save(AppSettings.Default with { Language = "a" }))
        {
            IsBackground = true
        };
        writerA.Start();
        Assert.True(firstDelivering.Wait(TimeSpan.FromSeconds(5)));

        var writerB = new Thread(() => sut.Save(AppSettings.Default with { Language = "b" }))
        {
            IsBackground = true
        };
        writerB.Start();

        // B hands its snapshot to the active drainer rather than blocking behind a slow
        // subscriber. What it must NOT do is announce "b" while A is still delivering "a" — a
        // subscriber would see the newer value first. Publishing inline fails right here.
        Assert.True(writerB.Join(TimeSpan.FromSeconds(5)));
        lock (received)
        {
            Assert.Equal(["a"], received);
        }

        releaseFirst.Set();
        Assert.True(writerA.Join(TimeSpan.FromSeconds(5)));

        // A's drainer picks up B's queued snapshot before it returns.
        lock (received)
        {
            Assert.Equal(["a", "b"], received);
        }

        Assert.Equal("b", sut.Current.Language);
    }

    [Fact]
    public void Save_ConcurrentCalls_DeliverEveryCommitExactlyOnce()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default);

        const int writers = 16;
        const int savesPerWriter = 20;
        var received = 0;
        // ReSharper disable once AccessToModifiedClosure -- the handler increments the captured counter from many threads (Interlocked); it is read only after every writer has joined.
        sut.SettingsChanged += _ => Interlocked.Increment(ref received);

        RunConcurrently(
            writers,
            idx =>
            {
                for (var i = 0; i < savesPerWriter; i++)
                {
                    sut.Save(AppSettings.Default with { CommandKeyphrase = $"phrase-{idx}-{i}" });
                }
            }
        );

        // Once every writer has returned no drainer can still be active, so the queue must have
        // been fully delivered. Asserts that invariant rather than reproducing the narrow resign
        // race, which needs an interleaving too tight to force without a production seam.
        Assert.Equal(writers * savesPerWriter, Volatile.Read(ref received));
    }

    [Fact]
    public void Save_FromInsideASubscriber_DeliversNestedCommitAfterTheCurrentOne()
    {
        var sut = new SettingsService(_filePath);
        var received = new List<string?>();
        var reentered = false;

        sut.SettingsChanged += s =>
        {
            lock (received)
            {
                received.Add(s.Language);
            }

            if (reentered || s.Language != "a")
            {
                return;
            }

            // Re-entrant save from inside a handler: it must queue, not recurse and deliver "b"
            // to the remaining subscribers before "a" has finished going out.
            reentered = true;
            sut.Save(AppSettings.Default with { Language = "b" });

            lock (received)
            {
                Assert.Equal(["a"], received);
            }
        };

        sut.Save(AppSettings.Default with { Language = "a" });

        lock (received)
        {
            Assert.Equal(["a", "b"], received);
        }
    }

    [Fact]
    public void Save_ConcurrentCalls_DoNotThrow()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default); // ensure the primary file exists before racing

        var exception = Record.Exception(() =>
            RunConcurrently(
                24,
                idx => sut.Save(AppSettings.Default with { CommandKeyphrase = $"phrase-{idx}" })
            )
        );

        Assert.Null(exception);

        // Reload must see a fully-formed, non-torn snapshot (a corrupted temp/primary file would
        // silently fall back to AppSettings.Default inside SettingsService.Load()).
        var reloaded = new SettingsService(_filePath);
        Assert.StartsWith("phrase-", reloaded.Current.CommandKeyphrase);
    }

    [Fact]
    public void Update_ConcurrentDisjointMutations_AllSurvive()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default);

        const int taskCount = 20;
        RunConcurrently(
            taskCount,
            idx => sut.Update(current => current with
            {
                AppInsertionStrategies = new Dictionary<string, TextInsertionStrategy>(
                    current.AppInsertionStrategies,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [$"app{idx}"] = TextInsertionStrategy.DirectTyping
                }
            })
        );

        var reloaded = new SettingsService(_filePath);
        for (var i = 0; i < taskCount; i++)
        {
            Assert.True(
                reloaded.Current.AppInsertionStrategies.ContainsKey($"app{i}"),
                $"app{i} was lost — Update did not read the latest Current under the write lock.");
        }
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
