using System.Text.Json;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxPreferencesServiceTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Save_TempPathRoundTrip_PublishesCurrentBeforeChangedAndLeavesNoTempFile()
    {
        using var directory = new TempDirectory("linux-preferences-round-trip");
        var path = Path.Join(directory.Path, "linux-preferences.json");
        var expected = new LinuxPreferences
        {
            CloseToTray = true,
            CheckForUpdatesOnStartup = false,
            LastUpdateCheckUtc = new DateTime(2026, 7, 18, 12, 34, 56, DateTimeKind.Utc),
            LastKnownLatestVersion = "1.2.3",
            LastKnownLatestUrl = "https://example.com/releases/1.2.3",
            DismissedUpdateVersion = null,
        };
        var service = new LinuxPreferencesService(path);
        var changedCount = 0;
        LinuxPreferences? notified = null;
        LinuxPreferences? currentObservedByHandler = null;
        service.Changed += next =>
        {
            changedCount++;
            notified = next;
            currentObservedByHandler = service.Current;
        };

        service.Save(expected);

        Assert.True(File.Exists(path));
        Assert.Equal(expected, Deserialize(path));
        Assert.Equal(expected, new LinuxPreferencesService(path).Current);
        Assert.Equal(expected, service.Current);
        Assert.Equal(1, changedCount);
        Assert.Equal(expected, notified);
        Assert.Equal(expected, currentObservedByHandler);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Update_ConcurrentDisjointMutations_UseLatestCommittedSnapshot()
    {
        using var directory = new TempDirectory("linux-preferences-updates");
        using var writer = new BlockingAtomicWriter();
        var path = Path.Join(directory.Path, "linux-preferences.json");
        var service = new LinuxPreferencesService(path, writer.Write);
        var secondCallerStarted = CreateCompletionSource();
        var secondMutatorEntered = CreateCompletionSource();
        var secondCompletion = new TaskCompletionSource<LinuxPreferences>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Task<LinuxPreferences>? firstUpdate = null;
        Thread? secondThread = null;
        bool secondReachedGateOrMutator;
        bool secondMutatorEnteredBeforeRelease;

        try
        {
            firstUpdate = Task.Run(() =>
                service.Update(current => current with { CloseToTray = true })
            );
            await writer.FirstEntered.WaitAsync(s_testGuard);

            secondThread = new Thread(() =>
            {
                secondCallerStarted.TrySetResult();
                try
                {
                    var result = service.Update(current =>
                    {
                        secondMutatorEntered.TrySetResult();
                        return current with { DismissedUpdateVersion = "1.2.3" };
                    });
                    secondCompletion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    secondCompletion.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
            };
            secondThread.Start();
            await secondCallerStarted.Task.WaitAsync(s_testGuard);

            secondReachedGateOrMutator = SpinWait.SpinUntil(
                () =>
                    secondMutatorEntered.Task.IsCompleted
                    || IsWaiting(secondThread)
                    || !secondThread.IsAlive,
                s_testGuard
            );
            secondMutatorEnteredBeforeRelease = secondMutatorEntered.Task.IsCompleted;
        }
        finally
        {
            writer.ReleaseFirst();
            await CompleteBestEffort(firstUpdate, secondCompletion.Task);
            if (secondThread is { IsAlive: true })
            {
                secondThread.Join(s_testGuard);
            }
        }

        var results = await Task.WhenAll(firstUpdate, secondCompletion.Task)
            .WaitAsync(s_testGuard);

        Assert.True(secondReachedGateOrMutator);
        Assert.False(secondMutatorEnteredBeforeRelease);
        Assert.False(writer.SecondEnteredBeforeFirstRelease);
        Assert.Equal(1, writer.MaximumConcurrency);
        Assert.Equal(
            new LinuxPreferences { CloseToTray = true },
            results[0]
        );
        var expected = new LinuxPreferences
        {
            CloseToTray = true, DismissedUpdateVersion = "1.2.3",
        };
        Assert.Equal(expected, results[1]);
        Assert.Equal(expected, service.Current);
        Assert.Equal(expected, new LinuxPreferencesService(path).Current);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Save_ConcurrentFullSnapshots_AreSerializedAndRemainWhole()
    {
        using var directory = new TempDirectory("linux-preferences-saves");
        using var writer = new BlockingAtomicWriter();
        var path = Path.Join(directory.Path, "linux-preferences.json");
        var service = new LinuxPreferencesService(path, writer.Write);
        var first = new LinuxPreferences
        {
            CloseToTray = true,
            LastKnownLatestVersion = "first",
            LastKnownLatestUrl = "https://example.com/first",
        };
        var second = new LinuxPreferences
        {
            CheckForUpdatesOnStartup = false,
            DismissedUpdateVersion = "second",
        };
        var secondCallerStarted = CreateCompletionSource();
        var secondCompletion = CreateCompletionSource();
        Task? firstSave = null;
        Thread? secondThread = null;
        bool secondReachedGateOrWriter;

        try
        {
            firstSave = Task.Run(() => service.Save(first));
            await writer.FirstEntered.WaitAsync(s_testGuard);

            secondThread = new Thread(() =>
            {
                secondCallerStarted.TrySetResult();
                try
                {
                    service.Save(second);
                    secondCompletion.TrySetResult();
                }
                catch (Exception ex)
                {
                    secondCompletion.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
            };
            secondThread.Start();
            await secondCallerStarted.Task.WaitAsync(s_testGuard);

            secondReachedGateOrWriter = SpinWait.SpinUntil(
                () =>
                    // ReSharper disable once AccessToDisposedClosure -- SpinUntil runs synchronously to completion before the enclosing method disposes writer.
                    writer.SecondEntered.IsCompleted
                    || IsWaiting(secondThread)
                    || !secondThread.IsAlive,
                s_testGuard
            );
        }
        finally
        {
            writer.ReleaseFirst();
            await CompleteBestEffort(firstSave, secondCompletion.Task);
            if (secondThread is { IsAlive: true })
            {
                secondThread.Join(s_testGuard);
            }
        }

        await Task.WhenAll(firstSave, secondCompletion.Task).WaitAsync(s_testGuard);
        var onDisk = Deserialize(path);

        Assert.True(secondReachedGateOrWriter);
        Assert.False(writer.SecondEnteredBeforeFirstRelease);
        Assert.Equal(1, writer.MaximumConcurrency);
        Assert.True(onDisk == first || onDisk == second);
        Assert.Equal(onDisk, service.Current);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void Save_WhenRealAtomicStagingFails_PreservesDiskAndCacheAndThrows()
    {
        var oldPreferences = new LinuxPreferences
        {
            CloseToTray = true,
            LastKnownLatestVersion = "old",
            LastKnownLatestUrl = "https://example.com/old",
        };
        using var failurePath = new MaximumFileNameTestPath(Serialize(oldPreferences));
        var service = new LinuxPreferencesService(failurePath.FilePath);
        var before = File.ReadAllBytes(failurePath.FilePath);
        var changedCount = 0;
        service.Changed += _ => changedCount++;
        var replacement = oldPreferences with
        {
            CloseToTray = false, LastKnownLatestVersion = "new",
        };

        Assert.ThrowsAny<IOException>(() => service.Save(replacement));

        Assert.Equal(before, File.ReadAllBytes(failurePath.FilePath));
        Assert.Equal(oldPreferences, Deserialize(failurePath.FilePath));
        Assert.Equal(oldPreferences, service.Current);
        Assert.Equal(0, changedCount);
        Assert.Empty(failurePath.TemporaryFiles);
    }

    [Fact]
    public void Save_WhenInjectedWriterFails_PreservesDiskAndCacheAndThrowsSameException()
    {
        using var directory = new TempDirectory("linux-preferences-writer-failure");
        var path = Path.Join(directory.Path, "linux-preferences.json");
        var oldPreferences = new LinuxPreferences
        {
            CloseToTray = true, DismissedUpdateVersion = "old",
        };
        new LinuxPreferencesService(path).Save(oldPreferences);
        var before = File.ReadAllBytes(path);
        var expectedException = new IOException("Injected write failure.");
        var service = new LinuxPreferencesService(path, (_, _) => throw expectedException);
        var changedCount = 0;
        service.Changed += _ => changedCount++;

        var actualException = Assert.Throws<IOException>(() =>
            service.Save(oldPreferences with { DismissedUpdateVersion = "new" })
        );

        Assert.Same(expectedException, actualException);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(oldPreferences, service.Current);
        Assert.Equal(0, changedCount);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    private static LinuxPreferences Deserialize(string path)
    {
        return JsonSerializer.Deserialize<LinuxPreferences>(
                   File.ReadAllText(path),
                   s_jsonOptions
               )
               ?? throw new InvalidOperationException("Preferences JSON was null.");
    }

    private static string Serialize(LinuxPreferences preferences)
    {
        return JsonSerializer.Serialize(preferences, s_jsonOptions);
    }

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsWaiting(Thread thread)
    {
        return (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
    }

    private static async Task CompleteBestEffort(params Task?[] tasks)
    {
        var activeTasks = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        if (activeTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(activeTasks).WaitAsync(s_testGuard);
        }
        catch
        {
            // Best-effort bounded completion before temp-directory cleanup.
        }
    }

    private sealed class BlockingAtomicWriter : IDisposable
    {
        private readonly TaskCompletionSource _firstEntered = CreateCompletionSource();
        private readonly ManualResetEventSlim _releaseFirst = new(false);
        private readonly TaskCompletionSource _secondEntered = CreateCompletionSource();
        private int _activeWriters;
        private int _firstReleased;
        private int _invocations;
        private int _maximumConcurrency;
        private int _secondEnteredBeforeFirstRelease;

        public Task FirstEntered => _firstEntered.Task;
        public Task SecondEntered => _secondEntered.Task;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public bool SecondEnteredBeforeFirstRelease =>
            Volatile.Read(ref _secondEnteredBeforeFirstRelease) != 0;

        public void Write(string path, string contents)
        {
            var invocation = Interlocked.Increment(ref _invocations);
            var activeWriters = Interlocked.Increment(ref _activeWriters);
            UpdateMaximum(activeWriters);
            try
            {
                if (invocation == 1)
                {
                    _firstEntered.TrySetResult();
                    if (!_releaseFirst.Wait(s_testGuard))
                    {
                        throw new TimeoutException("The first atomic writer was not released.");
                    }
                }
                else
                {
                    if (Volatile.Read(ref _firstReleased) == 0)
                    {
                        Interlocked.Exchange(ref _secondEnteredBeforeFirstRelease, 1);
                    }

                    _secondEntered.TrySetResult();
                }

                AtomicFileWrite.WriteAllText(path, contents);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWriters);
            }
        }

        public void ReleaseFirst()
        {
            Volatile.Write(ref _firstReleased, 1);
            _releaseFirst.Set();
        }

        public void Dispose()
        {
            ReleaseFirst();
            _releaseFirst.Dispose();
        }

        private void UpdateMaximum(int activeWriters)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (
                activeWriters > current
                && Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    activeWriters,
                    current
                ) != current
            )
            {
                current = Volatile.Read(ref _maximumConcurrency);
            }
        }
    }

    private sealed class MaximumFileNameTestPath : IDisposable
    {
        public MaximumFileNameTestPath(string contents)
        {
            DirectoryPath = TestPaths.CreateTempDirectory(
                "linux-preferences-atomic-failure"
            );
            var fileNameLength = FindMaximumFileNameLength();
            FilePath = Path.Join(DirectoryPath, new string('x', fileNameLength));
            File.WriteAllText(FilePath, contents);
        }

        private string DirectoryPath { get; }
        public string FilePath { get; }
        public IEnumerable<string> TemporaryFiles =>
            Directory.EnumerateFiles(DirectoryPath, "*.tmp");

        public void Dispose()
        {
            try
            {
                TestPaths.DeleteDirectory(DirectoryPath);
            }
            catch
            {
                // Best-effort cleanup for a temp test directory.
            }
        }

        private int FindMaximumFileNameLength()
        {
            var low = 1;
            var high = 128;
            while (CanCreateFile(high))
            {
                low = high;
                high *= 2;
                if (high > 16_384)
                {
                    throw new InvalidOperationException(
                        "Could not find the temporary filesystem path limit."
                    );
                }
            }

            while (low + 1 < high)
            {
                var middle = low + (high - low) / 2;
                if (CanCreateFile(middle))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private bool CanCreateFile(int fileNameLength)
        {
            var path = Path.Join(DirectoryPath, new string('x', fileNameLength));
            var created = false;
            try
            {
                File.WriteAllText(path, "probe");
                created = true;
            }
            catch
            {
                // The first failing length provides the upper bound for the search.
            }
            finally
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort cleanup for a probe file.
                }
            }

            return created;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string name)
        {
            Path = TestPaths.CreateTempDirectory(name);
        }

        public string Path { get; }

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
