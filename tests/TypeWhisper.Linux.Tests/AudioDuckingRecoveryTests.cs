using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AudioDuckingRecoveryTests
{
    private const string ServerCookie = "3922:700c";
    private const string Firefox = "sink-input-by-application-name:Firefox";
    private static readonly string[] s_list = ["list", "sink-inputs"];

    [Fact]
    public void RestoreAudio_leaves_a_stream_the_user_changed_while_ducked()
    {
        var runner = CreateRunner(Stream(10, 40000), Stream(10, 50000));
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>());

        service.DuckAudio(0.5f);
        service.RestoreAudio();

        Assert.Equal(1, CountSets(runner, "10", "20000"));
        Assert.Equal(0, CountSets(runner, "10", "40000"));
    }

    [Fact]
    public void A_stream_that_starts_mid_recording_is_lowered_and_restored()
    {
        var watcher = new FakeStreamWatcher();
        var runner = CreateRunner(
            Stream(10, 40000),
            Stream(10, 20000) + Stream(11, 30000),
            Stream(10, 20000) + Stream(11, 15000)
        );
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), newStreamWatcher: watcher);

        service.DuckAudio(0.5f);
        watcher.RaiseNewStream();
        service.RestoreAudio();

        Assert.Equal(1, CountSets(runner, "11", "15000"));
        Assert.Equal(1, CountSets(runner, "10", "40000"));
        Assert.Equal(1, CountSets(runner, "11", "30000"));
        Assert.Equal(0, CountSets(runner, "10", "10000"));
    }

    [Fact]
    public void A_new_stream_event_outside_a_recording_runs_nothing()
    {
        var watcher = new FakeStreamWatcher();
        var runner = CreateRunner(Stream(10, 40000));
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), newStreamWatcher: watcher);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        var invocations = runner.InvocationsSnapshot().Length;
        watcher.RaiseNewStream();

        Assert.Equal(invocations, runner.InvocationsSnapshot().Length);
    }

    [Fact]
    public void TypeWhispers_own_streams_are_never_lowered()
    {
        // 10 plays in-process, 11 through a helper TypeWhisper launched, 12 is another app.
        var runner = CreateRunner(
            Stream(10, 40000, processId: 4242) + Stream(11, 30000, processId: 5000) + Stream(12, 30000, processId: 7)
        );
        var service = new AudioDuckingService(
            runner,
            Mock.Of<IErrorLogService>(),
            ownProcessId: 4242,
            parentProcessId: processId => processId == "5000" ? "4242" : "1"
        );

        service.DuckAudio(0.5f);

        Assert.Equal(0, CountSets(runner, "10", "20000"));
        Assert.Equal(0, CountSets(runner, "11", "15000"));
        Assert.Equal(1, CountSets(runner, "12", "15000"));
    }

    [Fact]
    public void A_ducked_apps_next_stream_starting_at_the_ducked_level_is_not_lowered_twice()
    {
        var watcher = new FakeStreamWatcher();
        var runner = CreateRunner(
            Stream(10, 40000, Firefox),
            Stream(10, 20000, Firefox) + Stream(11, 20000, Firefox),
            Stream(10, 20000, Firefox) + Stream(11, 20000, Firefox)
        );
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), newStreamWatcher: watcher);

        service.DuckAudio(0.5f);
        watcher.RaiseNewStream();
        service.RestoreAudio();

        Assert.Equal(0, CountSets(runner, "11", "10000"));
        Assert.Equal(1, CountSets(runner, "10", "40000"));
        Assert.Equal(1, CountSets(runner, "11", "40000"));
    }

    [Fact]
    public void A_stream_that_ends_while_ducked_is_restored_when_its_app_plays_again()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var watcher = new FakeStreamWatcher();
        var runner = CreateRunner(Stream(10, 40000, Firefox), string.Empty, string.Empty, Stream(12, 20000, Firefox));
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal, watcher);

        service.DuckAudio(0.5f);
        Assert.Contains(Firefox, File.ReadAllText(journal), StringComparison.Ordinal);
        service.RestoreAudio();

        // The server will start Firefox's next stream at the ducked level, so the entry is kept.
        Assert.Contains(Firefox, File.ReadAllText(journal), StringComparison.Ordinal);
        watcher.RaiseNewStream();

        Assert.Equal(1, CountSets(runner, "12", "40000"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void An_apps_replacement_stream_already_playing_at_restore_is_repaired_without_waiting_for_an_event()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var runner = CreateRunner(Stream(10, 40000, Firefox), Stream(12, 20000, Firefox));
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);

        service.DuckAudio(0.5f);
        service.RestoreAudio();

        Assert.Equal(1, CountSets(runner, "12", "40000"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Two_streams_of_one_app_still_playing_after_a_crash_each_get_their_own_volume_back()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashed = new AudioDuckingService(
            CreateRunner(Stream(10, 40000, Firefox) + Stream(11, 60000, Firefox)),
            Mock.Of<IErrorLogService>(),
            journal
        );
        crashed.DuckAudio(0.5f);

        var runner = CreateRunner(Stream(10, 20000, Firefox) + Stream(11, 30000, Firefox));
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, CountSets(runner, "10", "40000"));
        Assert.Equal(1, CountSets(runner, "11", "60000"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task A_set_volume_still_running_when_shutdown_returns_stays_in_the_journal()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var runner = CreateRunner(Stream(10, 40000) + Stream(11, 30000));
        var duck11 = runner.Hold(["set-sink-input-volume", "11", "15000"]);
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);

        var duck = Task.Run(() => service.DuckAudio(0.5f));
        Assert.True(duck11.Launched.Wait(TimeSpan.FromSeconds(10)));
        await Task.Run(() => service.RestoreAudioForShutdown(TimeSpan.Zero)).WaitAsync(TimeSpan.FromSeconds(10));

        // The app may exit before the late set lands and is undone, so the next run must know.
        var entries = await File.ReadAllTextAsync(journal);
        Assert.Contains("\"11\"", entries, StringComparison.Ordinal);
        Assert.DoesNotContain("\"10\"", entries, StringComparison.Ordinal);

        duck11.ExitWith();
        await duck.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_stream_that_inherited_the_ducked_level_is_journaled()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var watcher = new FakeStreamWatcher();
        var runner = CreateRunner(Stream(10, 40000, Firefox), Stream(10, 20000, Firefox) + Stream(11, 20000, Firefox));
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal, watcher);

        service.DuckAudio(0.5f);
        watcher.RaiseNewStream();

        Assert.Contains("\"11\"", File.ReadAllText(journal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ended_streams_entry_never_claims_a_live_stream_that_has_its_own_entry()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashed = new AudioDuckingService(
            CreateRunner(Stream(10, 40000, Firefox) + Stream(11, 60000, Firefox)),
            Mock.Of<IErrorLogService>(),
            journal
        );
        crashed.DuckAudio(0.5f);

        // Stream 10 ended; the user moved 11 to exactly the level 10 was ducked to.
        var runner = CreateRunner(Stream(11, 20000, Firefox));
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(runner.InvocationsSnapshot(), invocation => invocation.Args[0] == "set-sink-input-volume");
    }

    [Fact]
    public void A_stream_whose_restore_keeps_failing_stays_in_the_journal()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var runner = CreateRunner(Stream(10, 40000));
        runner.FailWhen((_, args) => args.SequenceEqual(["set-sink-input-volume", "10", "40000"]), "restore denied");
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();
        service.RestoreAudio();

        Assert.Equal(3, CountSets(runner, "10", "40000"));
        Assert.Contains("\"10\"", File.ReadAllText(journal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replacement_stream_is_repaired_once_even_when_its_restored_level_matches_another_entry()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashed = new AudioDuckingService(
            CreateRunner(Stream(10, 64000, Firefox) + Stream(11, 80000, Firefox)),
            Mock.Of<IErrorLogService>(),
            journal
        );
        crashed.DuckAudio(0.8f);

        var watcher = new FakeStreamWatcher();
        var runner = CreateRunnerOnServer("296a:0996", Stream(20, 51200, Firefox), Stream(20, 64000, Firefox));
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal, watcher);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));
        watcher.RaiseNewStream();

        Assert.Equal(1, CountSets(runner, "20", "64000"));
        Assert.Equal(0, CountSets(runner, "20", "80000"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task A_replacement_adopted_during_a_recording_is_not_restored_twice_afterwards()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashed = new AudioDuckingService(
            CreateRunner(Stream(10, 64000, Firefox) + Stream(11, 80000, Firefox)),
            Mock.Of<IErrorLogService>(),
            journal
        );
        crashed.DuckAudio(0.8f);

        var watcher = new FakeStreamWatcher();
        var runner = CreateRunnerOnServer(
            "296a:0996",
            string.Empty,
            Stream(20, 51200, Firefox),
            Stream(20, 51200, Firefox),
            Stream(20, 64000, Firefox)
        );
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal, watcher);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));
        restarted.DuckAudio(0.8f);
        restarted.RestoreAudio();
        watcher.RaiseNewStream();

        Assert.Equal(1, CountSets(runner, "20", "64000"));
        Assert.Equal(0, CountSets(runner, "20", "80000"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task A_sibling_that_inherited_the_ducked_level_before_a_crash_is_repaired_with_its_app()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashed = new AudioDuckingService(CreateRunner(Stream(10, 65536, Firefox)), Mock.Of<IErrorLogService>(), journal);
        crashed.DuckAudio(0.5f);

        var runner = CreateRunner(Stream(10, 32768, Firefox) + Stream(11, 32768, Firefox));
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, CountSets(runner, "10", "65536"));
        Assert.Equal(1, CountSets(runner, "11", "65536"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void Every_duck_restarts_a_stream_watcher_that_stopped()
    {
        var watcher = new FakeStreamWatcher();
        var service = new AudioDuckingService(CreateRunner(Stream(10, 40000)), Mock.Of<IErrorLogService>(), newStreamWatcher: watcher);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.DuckAudio(0.5f);

        Assert.Equal(2, watcher.StartCount);
    }

    [Fact]
    public async Task A_run_that_ended_while_ducked_is_repaired_by_the_next_run_after_a_server_restart()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashedRunner = CreateRunner(Stream(10, 40000, Firefox));
        var crashed = new AudioDuckingService(crashedRunner, Mock.Of<IErrorLogService>(), journal);
        crashed.DuckAudio(0.5f);

        var runner = CreateRunnerOnServer("296a:0996", Stream(77, 20000, Firefox) + Stream(78, 20000));
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, CountSets(runner, "77", "40000"));
        // Id 78 has no identity and belongs to a different server run; it is not ours.
        Assert.Equal(0, CountSets(runner, "78", "40000"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task A_stream_still_playing_after_a_crash_is_repaired_by_id()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashed = new AudioDuckingService(CreateRunner(Stream(10, 40000)), Mock.Of<IErrorLogService>(), journal);
        crashed.DuckAudio(0.5f);

        var runner = CreateRunner(Stream(10, 20000));
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, CountSets(runner, "10", "40000"));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task A_remembered_level_the_user_changed_is_dropped_without_touching_the_stream()
    {
        using var directory = new TempDirectory();
        var journal = Path.Join(directory.Path, AudioDuckingService.JournalFileName);
        var crashed = new AudioDuckingService(
            CreateRunner(Stream(10, 40000, Firefox)),
            Mock.Of<IErrorLogService>(),
            journal
        );
        crashed.DuckAudio(0.5f);

        var runner = CreateRunnerOnServer("296a:0996", Stream(77, 50000, Firefox));
        var restarted = new AudioDuckingService(runner, Mock.Of<IErrorLogService>(), journal);
        await restarted.StartupRepair.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(runner.InvocationsSnapshot(), invocation => invocation.Args[0] == "set-sink-input-volume");
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void Nothing_is_lowered_when_the_journal_cannot_be_written()
    {
        using var directory = new TempDirectory();
        var blocker = Path.Join(directory.Path, "not-a-directory");
        File.WriteAllText(blocker, string.Empty);
        var runner = CreateRunner(Stream(10, 40000));
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(
            runner,
            errorLog.Object,
            Path.Join(blocker, AudioDuckingService.JournalFileName)
        );

        service.DuckAudio(0.5f);

        Assert.DoesNotContain(runner.InvocationsSnapshot(), invocation => invocation.Args[0] == "set-sink-input-volume");
        errorLog.Verify(
            log => log.AddEntry(It.Is<string>(message => message.Contains("journal", StringComparison.Ordinal)), It.IsAny<string>()),
            Times.Once
        );
    }

    [Theory]
    [InlineData("Event 'new' on sink-input #93", true)]
    [InlineData("Event 'change' on sink-input #93", false)]
    [InlineData("Event 'remove' on sink-input #93", false)]
    [InlineData("Event 'new' on source-output #12", false)]
    [InlineData(null, false)]
    public void Only_new_sink_inputs_wake_the_ducking_service(string? line, bool relevant)
    {
        Assert.Equal(relevant, AudioDuckingService.IsNewSinkInputEvent(line));
    }

    private static string Stream(int id, int volume, string? identity = null, int? processId = null)
    {
        var properties = processId is null ? string.Empty : $"        application.process.id = \"{processId}\"\n";
        if (identity is not null)
        {
            properties += $"        module-stream-restore.id = \"{identity}\"\n";
        }

        return $"Sink Input #{id}\n    Volume: mono: {volume} / 61% / -12.00 dB\n    Properties:\n{properties}";
    }

    private static FakeProcessRunner CreateRunner(params string[] listings)
    {
        return CreateRunnerOnServer(ServerCookie, listings);
    }

    // Each listing answers one `pactl list sink-inputs` call in order; the last one repeats.
    private static FakeProcessRunner CreateRunnerOnServer(string cookie, params string[] listings)
    {
        var runner = new FakeProcessRunner();
        var listCalls = 0;
        for (var index = 0; index < listings.Length; index++)
        {
            var call = index;
            var last = index == listings.Length - 1;
            runner.RespondWith(
                (_, args) =>
                {
                    if (!args.SequenceEqual(s_list) || (listCalls != call && !last))
                    {
                        return false;
                    }

                    listCalls++;
                    return true;
                },
                listings[index]
            );
        }

        runner.RespondWith((_, args) => args.SequenceEqual(["info"]), $"Cookie: {cookie}\n");
        return runner;
    }

    private static int CountSets(FakeProcessRunner runner, string inputId, string volume)
    {
        return runner.InvocationsSnapshot()
            .Count(invocation => invocation.Args.SequenceEqual(["set-sink-input-volume", inputId, volume]));
    }

    private sealed class FakeStreamWatcher : IDefaultDeviceChangeWatcher
    {
        private Action? _onNewStream;

        public int StartCount { get; private set; }

        public void Start(Action onDefaultDeviceChanged)
        {
            StartCount++;
            _onNewStream = onDefaultDeviceChanged;
        }

        public void Stop()
        {
            _onNewStream = null;
        }

        public void Dispose()
        {
            Stop();
        }

        public void RaiseNewStream()
        {
            Assert.NotNull(_onNewStream);
            _onNewStream();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = TestPaths.CreateTempDirectory("audio-ducking");

        public void Dispose()
        {
            try
            {
                TestPaths.DeleteDirectory(Path);
            }
            catch
            {
                // Best-effort cleanup for a temp test directory.
            }
        }
    }
}
