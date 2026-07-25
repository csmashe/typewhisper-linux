// ReSharper disable MethodSupportsCancellation -- every WaitAsync here uses only a test-guard timeout; there is no ambient cancellation token to pass.
// ReSharper disable MethodHasAsyncOverload -- synchronous File.Read/WriteAllText is deliberate in these test assertions; the async overload would only add await noise with no benefit off the hot path.
using System.Collections.Concurrent;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class WatchFolderServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.WatchFolderServiceTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task Start_WhenSourceBasenamesCollide_CommitsDistinctExportsBeforeDeletingSources()
    {
        var watchPath = Path.Join(_tempDir, "watch");
        var outputPath = Path.Join(_tempDir, "output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var wavPath = Path.Join(watchPath, "meeting.wav");
        var mp3Path = Path.Join(watchPath, "meeting.mp3");
        File.WriteAllBytes(wavPath, [1, 2, 3]);
        File.WriteAllBytes(mp3Path, [4, 5, 6]);

        await using var service = new WatchFolderService(Path.Join(_tempDir, "data"));
        var processed = await StartAndWaitForProcessedItemsAsync(
            service,
            expectedCount: 2,
            new WatchFolderOptions(
                watchPath,
                outputPath,
                WatchFolderOutputFormat.Markdown,
                DeleteSource: true
            )
        );
        service.Stop();

        Assert.All(processed, item => Assert.True(item.Success, item.ErrorMessage));
        Assert.Equal(
            [Path.Join(outputPath, "meeting (1).md"), Path.Join(outputPath, "meeting.md")],
            processed.Select(item => item.OutputPath).Order()
        );
        Assert.All(processed, item => Assert.True(File.Exists(item.OutputPath)));
        Assert.False(File.Exists(wavPath));
        Assert.False(File.Exists(mp3Path));
    }

    [Fact]
    public async Task Start_WhenUserExportsExist_PreservesBytesAndAdvancesSuffix()
    {
        var watchPath = Path.Join(_tempDir, "watch");
        var outputPath = Path.Join(_tempDir, "output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var sourcePath = Path.Join(watchPath, "meeting.wav");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        var baseOutputPath = Path.Join(outputPath, "meeting.txt");
        var firstSuffixPath = Path.Join(outputPath, "meeting (1).txt");
        File.WriteAllBytes(baseOutputPath, [0, 1, 2, 255]);
        File.WriteAllBytes(firstSuffixPath, [255, 2, 1, 0]);
        var baseBytes = File.ReadAllBytes(baseOutputPath);
        var firstSuffixBytes = File.ReadAllBytes(firstSuffixPath);

        await using var service = new WatchFolderService(Path.Join(_tempDir, "data"));
        var processed = await StartAndWaitForProcessedItemsAsync(
            service,
            expectedCount: 1,
            new WatchFolderOptions(
                watchPath,
                outputPath,
                WatchFolderOutputFormat.PlainText,
                DeleteSource: false
            )
        );
        service.Stop();

        var item = Assert.Single(processed);
        Assert.True(item.Success, item.ErrorMessage);
        Assert.Equal(Path.Join(outputPath, "meeting (2).txt"), item.OutputPath);
        Assert.Equal("Transcribed meeting.wav", File.ReadAllText(item.OutputPath));
        Assert.Equal(baseBytes, File.ReadAllBytes(baseOutputPath));
        Assert.Equal(firstSuffixBytes, File.ReadAllBytes(firstSuffixPath));
    }

    [Fact]
    public async Task Start_WhenExportNameIsOccupiedByDirectory_AdvancesSuffix()
    {
        var watchPath = Path.Join(_tempDir, "watch");
        var outputPath = Path.Join(_tempDir, "output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var sourcePath = Path.Join(watchPath, "meeting.wav");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        Directory.CreateDirectory(Path.Join(outputPath, "meeting.txt"));

        await using var service = new WatchFolderService(Path.Join(_tempDir, "data"));
        var processed = await StartAndWaitForProcessedItemsAsync(
            service,
            expectedCount: 1,
            new WatchFolderOptions(
                watchPath,
                outputPath,
                WatchFolderOutputFormat.PlainText,
                DeleteSource: false
            )
        );
        service.Stop();

        var item = Assert.Single(processed);
        Assert.True(item.Success, item.ErrorMessage);
        Assert.Equal(Path.Join(outputPath, "meeting (1).txt"), item.OutputPath);
        Assert.Equal("Transcribed meeting.wav", File.ReadAllText(item.OutputPath));
        Assert.True(Directory.Exists(Path.Join(outputPath, "meeting.txt")));
    }

    [Fact]
    public async Task ProviderCancellation_WithLiveRun_RecordsFailureAndContinuesQueue()
    {
        var watchPath = Path.Join(_tempDir, "provider-cancellation-watch");
        var outputPath = Path.Join(_tempDir, "provider-cancellation-output");
        var dataPath = Path.Join(_tempDir, "provider-cancellation-data");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var timeoutPath = Path.Join(watchPath, "a-timeout.wav");
        var nextPath = Path.Join(watchPath, "b-next.wav");
        File.WriteAllBytes(timeoutPath, [1, 2, 3]);
        File.WriteAllBytes(nextPath, [4, 5, 6]);

        using var privateCancellation = new CancellationTokenSource();
        var timeoutEntered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseTimeout = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var twoItemsProcessed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var calls = new ConcurrentQueue<string>();
        var processed = new ConcurrentQueue<WatchFolderHistoryItem>();
        var service = new WatchFolderService(dataPath);
        WatchFolderService.WatchFolderRun? originalRun = null;
        service.FileProcessed += (_, item) =>
        {
            processed.Enqueue(item);
            if (processed.Count >= 2)
            {
                twoItemsProcessed.TrySetResult(true);
            }
        };

        try
        {
            service.Start(
                CreateOptions(watchPath, outputPath),
                async (request, ct) =>
                {
                    var fileName = Path.GetFileName(request.FilePath);
                    calls.Enqueue(fileName);
                    if (fileName == "a-timeout.wav")
                    {
                        timeoutEntered.TrySetResult(ct);
                        await releaseTimeout.Task;
                        // ReSharper disable once AccessToDisposedClosure -- the finally awaits StopAsync/WorkerCompletion, so this callback finishes before the `using var privateCancellation` is disposed at scope end.
                        throw new OperationCanceledException(
                            "Provider request timed out.",
                            privateCancellation.Token
                        );
                    }

                    return CreateResult(request);
                }
            );

            originalRun = service.CurrentRun;
            Assert.NotNull(originalRun);
            var runToken = await timeoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(runToken.IsCancellationRequested);

            privateCancellation.Cancel();
            releaseTimeout.TrySetResult(true);
            await twoItemsProcessed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(["a-timeout.wav", "b-next.wav"], calls);
            Assert.Equal(2, service.History.Count);
            Assert.Equal(2, processed.Count);

            var failedItem = Assert.Single(service.History, item => !item.Success);
            Assert.Equal("a-timeout.wav", failedItem.FileName);
            Assert.Empty(failedItem.OutputPath);
            Assert.False(string.IsNullOrWhiteSpace(failedItem.ErrorMessage));
            Assert.Contains("timed out", failedItem.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            var failedEvent = Assert.Single(processed, item => !item.Success);
            Assert.Same(failedItem, failedEvent);
            var failedFingerprint = Assert.Single(originalRun.FailedFingerprints);
            Assert.StartsWith(
                $"{Path.GetFullPath(timeoutPath)}|",
                failedFingerprint,
                StringComparison.Ordinal
            );

            var successfulItem = Assert.Single(service.History, item => item.Success);
            Assert.Equal("b-next.wav", successfulItem.FileName);
            Assert.Equal(Path.Join(outputPath, "b-next.txt"), successfulItem.OutputPath);
            Assert.True(File.Exists(successfulItem.OutputPath));
            Assert.True(File.Exists(timeoutPath));
            Assert.False(File.Exists(Path.Join(outputPath, "a-timeout.txt")));

            Assert.True(service.IsRunning);
            Assert.False(runToken.IsCancellationRequested);
            Assert.Same(originalRun, service.CurrentRun);
            Assert.Null(originalRun.WorkerFailure);
            Assert.False(originalRun.WorkerCompletion.IsFaulted);
            Assert.False(originalRun.WorkerCompletion.IsCompleted);
        }
        finally
        {
            privateCancellation.Cancel();
            releaseTimeout.TrySetResult(true);
            if (service.CurrentRun is not null)
            {
                await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (originalRun is not null)
            {
                await originalRun.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunTokenCancellation_StopsQueueWithoutWorkerFailure()
    {
        var watchPath = Path.Join(_tempDir, "run-cancellation-watch");
        var outputPath = Path.Join(_tempDir, "run-cancellation-output");
        var dataPath = Path.Join(_tempDir, "run-cancellation-data");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var sourcePath = Path.Join(watchPath, "canceled.wav");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);

        var entered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var processed = new ConcurrentQueue<WatchFolderHistoryItem>();
        var service = new WatchFolderService(dataPath);
        WatchFolderService.WatchFolderRun? run = null;
        service.FileProcessed += (_, item) => processed.Enqueue(item);

        try
        {
            service.Start(
                CreateOptions(watchPath, outputPath),
                async (request, ct) =>
                {
                    entered.TrySetResult(ct);
                    await release.Task.WaitAsync(ct);
                    return CreateResult(request);
                }
            );

            run = service.CurrentRun;
            Assert.NotNull(run);
            var handlerToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            run.CancellationSource.Cancel();
            await run.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(handlerToken.IsCancellationRequested);
            Assert.Empty(service.History);
            Assert.Empty(processed);
            Assert.Empty(run.FailedFingerprints);
            Assert.Null(run.WorkerFailure);
            Assert.True(run.WorkerCompletion.IsCompletedSuccessfully);
            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(Path.Join(outputPath, "canceled.txt")));
            Assert.False(File.Exists(Path.Join(dataPath, "watch-folder-processed.json")));

            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            release.TrySetResult(true);
            if (service.CurrentRun is not null)
            {
                await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (run is not null)
            {
                await run.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueueWorkerFault_IsObservedAndMarksCurrentRunUnhealthy()
    {
        var watchPath = Path.Join(_tempDir, "worker-fault-watch");
        var outputPath = Path.Join(_tempDir, "worker-fault-output");
        var dataPath = Path.Join(_tempDir, "worker-fault-data");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);

        var healthTransition = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var processed = new ConcurrentQueue<WatchFolderHistoryItem>();
        var service = new WatchFolderService(dataPath);
        WatchFolderService.WatchFolderRun? run = null;
        service.FileProcessed += (_, item) => processed.Enqueue(item);
        service.StateChanged += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- the handler deliberately reads the current `run` (assigned after Start) to correlate the transition with the active run.
            var observedRun = run;
            if (
                observedRun is not null
                && ReferenceEquals(service.CurrentRun, observedRun)
                && !service.IsRunning
            )
            {
                healthTransition.TrySetResult(true);
            }
        };

        try
        {
            service.Start(CreateOptions(watchPath, outputPath), TranscribeAsync);
            run = service.CurrentRun;
            Assert.NotNull(run);

            run.PendingFiles.Enqueue("\0");
            await healthTransition.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await run.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Same(run, service.CurrentRun);
            Assert.False(service.IsRunning);
            Assert.Null(service.WatchPath);
            Assert.Null(service.CurrentlyProcessing);
            Assert.True(run.CancellationSource.IsCancellationRequested);
            Assert.IsAssignableFrom<ArgumentException>(run.WorkerFailure);
            Assert.True(run.WorkerCompletion.IsCompletedSuccessfully);
            Assert.Empty(service.History);
            Assert.Empty(processed);
            Assert.Empty(run.FailedFingerprints);
            Assert.Empty(Directory.EnumerateFiles(outputPath));
            Assert.False(File.Exists(Path.Join(dataPath, "watch-folder-processed.json")));

            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(service.CurrentRun);
        }
        finally
        {
            if (service.CurrentRun is not null)
            {
                await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (run is not null)
            {
                await run.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_InFlightHandler_AwaitsWorkerBeforeReturning()
    {
        var watchPath = Path.Join(_tempDir, "await-watch");
        var outputPath = Path.Join(_tempDir, "await-output");
        var dataPath = Path.Join(_tempDir, "await-data");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var sourcePath = Path.Join(watchPath, "blocked.wav");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);

        var entered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var processed = new ConcurrentQueue<WatchFolderHistoryItem>();
        var service = new WatchFolderService(dataPath);
        Task? stopTask = null;
        service.FileProcessed += (_, item) => processed.Enqueue(item);

        try
        {
            service.Start(
                CreateOptions(watchPath, outputPath, deleteSource: true),
                async (request, ct) =>
                {
                    entered.TrySetResult(ct);
                    await release.Task;
                    return CreateResult(request);
                }
            );

            var oldToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            stopTask = service.StopAsync();

            Assert.False(service.IsRunning);
            Assert.Null(service.WatchPath);
            Assert.Null(service.CurrentlyProcessing);
            Assert.True(oldToken.IsCancellationRequested);
            Assert.False(stopTask.IsCompleted);

            release.TrySetResult(true);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(Path.Join(outputPath, "blocked.txt")));
            Assert.Empty(service.History);
            Assert.Empty(processed);
            Assert.False(File.Exists(Path.Join(dataPath, "watch-folder-processed.json")));
        }
        finally
        {
            release.TrySetResult(true);
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task Restart_AfterBoundedDrain_UsesFreshRunAndLeavesOldQueuedWorkRetired()
    {
        var oldWatchPath = Path.Join(_tempDir, "restart-old-watch");
        var newWatchPath = Path.Join(_tempDir, "restart-new-watch");
        var outputPath = Path.Join(_tempDir, "restart-output");
        Directory.CreateDirectory(oldWatchPath);
        Directory.CreateDirectory(newWatchPath);
        Directory.CreateDirectory(outputPath);
        File.WriteAllBytes(Path.Join(oldWatchPath, "a-blocked.wav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Join(oldWatchPath, "b-old-pending.wav"), [4, 5, 6]);

        var oldEntered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseOld = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var newEntered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseNew = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var oldCalls = new ConcurrentQueue<string>();
        var newCalls = new ConcurrentQueue<string>();
        Task? retiredWorkers = null;
        TimeSpan? requestedDeadline = null;
        var retiredWorkersWereIncomplete = false;
        var waitCallCount = 0;

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- kept adjacent to the call sites and captured state below.
        Task WaitForWorkers(Task workers, TimeSpan timeout)
        {
            // ReSharper disable once InvertIf -- the first-call special case reads better as the guard.
            if (Interlocked.Increment(ref waitCallCount) == 1)
            {
                retiredWorkers = workers;
                requestedDeadline = timeout;
                retiredWorkersWereIncomplete = !workers.IsCompleted;
                return Task.FromException(new TimeoutException("Simulated worker drain timeout."));
            }

            return workers.WaitAsync(timeout);
        }

        var service = new WatchFolderService(
            Path.Join(_tempDir, "restart-data"),
            WaitForWorkers
        );
        WatchFolderService.WatchFolderRun? oldRun = null;

        try
        {
            service.Start(
                CreateOptions(oldWatchPath, outputPath),
                async (request, ct) =>
                {
                    var fileName = Path.GetFileName(request.FilePath);
                    oldCalls.Enqueue(fileName);
                    // ReSharper disable once InvertIf -- inverting would duplicate the `return CreateResult(request)` tail.
                    if (fileName == "a-blocked.wav")
                    {
                        oldEntered.TrySetResult(ct);
                        await releaseOld.Task;
                    }

                    return CreateResult(request);
                }
            );

            var oldToken = await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            oldRun = service.CurrentRun;
            Assert.NotNull(oldRun);
            var oldPendingFiles = oldRun.PendingFiles;
            var oldQueuedFiles = oldRun.QueuedFiles;
            var oldCancellationSource = oldRun.CancellationSource;
            var oldPendingPath = Path.GetFullPath(
                Path.Join(oldWatchPath, "b-old-pending.wav")
            );
            Assert.Contains(oldPendingPath, oldPendingFiles);
            Assert.True(oldQueuedFiles.ContainsKey(oldPendingPath));

            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(service.IsRunning);
            Assert.True(oldToken.IsCancellationRequested);
            Assert.True(retiredWorkersWereIncomplete);
            Assert.Equal(TimeSpan.FromSeconds(2), requestedDeadline);
            Assert.Same(oldRun.WorkerCompletion, retiredWorkers);
            Assert.False(retiredWorkers!.IsCompleted);
            // The retired-run observer is registered but cannot complete while the old
            // handler is still gated: this fails if the SetRetiredCleanup registration is dropped.
            Assert.False(oldRun.RetiredCleanup.IsCompleted);

            File.WriteAllBytes(Path.Join(newWatchPath, "new.wav"), [7, 8, 9]);
            service.Start(
                CreateOptions(newWatchPath, outputPath),
                async (request, ct) =>
                {
                    newCalls.Enqueue(Path.GetFileName(request.FilePath));
                    newEntered.TrySetResult(ct);
                    await releaseNew.Task;
                    return CreateResult(request);
                }
            );

            var newRun = service.CurrentRun;
            Assert.NotNull(newRun);
            var newToken = await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotSame(oldRun, newRun);
            Assert.NotSame(oldPendingFiles, newRun.PendingFiles);
            Assert.NotSame(oldQueuedFiles, newRun.QueuedFiles);
            Assert.NotSame(oldCancellationSource, newRun.CancellationSource);
            Assert.NotEqual(oldToken, newToken);
            Assert.True(oldToken.IsCancellationRequested);
            Assert.False(newToken.IsCancellationRequested);

            releaseOld.TrySetResult(true);
            await retiredWorkers.WaitAsync(TimeSpan.FromSeconds(15));
            await oldRun.RetiredCleanup.WaitAsync(TimeSpan.FromSeconds(15));

            // The retired observer disposes the old generation's CTS exactly once when its
            // workers settle; accessing the token afterward must throw.
            Assert.Throws<ObjectDisposedException>(() => _ = oldCancellationSource.Token);
            Assert.Equal(["a-blocked.wav"], oldCalls);
            Assert.Equal(["new.wav"], newCalls);
            Assert.Equal("new.wav", service.CurrentlyProcessing);
            Assert.True(service.OwnsActiveFile(newRun, Path.Join(newWatchPath, "new.wav")));
        }
        finally
        {
            releaseOld.TrySetResult(true);
            releaseNew.TrySetResult(true);
            var finalRun = service.CurrentRun;
            if (service.IsRunning)
            {
                await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (retiredWorkers is not null)
            {
                await retiredWorkers.WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (oldRun is not null)
            {
                await oldRun.RetiredCleanup.WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (finalRun is not null && !ReferenceEquals(finalRun, oldRun))
            {
                await finalRun.RetiredCleanup.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameFolderRestart_OldCompletionCannotOverlapOrPublishIntoNewRun()
    {
        var watchPath = Path.Join(_tempDir, "same-watch");
        var outputPath = Path.Join(_tempDir, "same-output");
        var dataPath = Path.Join(_tempDir, "same-data");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        var sharedPath = Path.Join(watchPath, "a-shared.wav");
        var newPath = Path.Join(watchPath, "z-new.wav");
        File.WriteAllBytes(sharedPath, [1, 2, 3]);

        var oldEntered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseOld = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var newEntered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseNew = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var newProcessed = new TaskCompletionSource<WatchFolderHistoryItem>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var oldCalls = new ConcurrentQueue<string>();
        var newCalls = new ConcurrentQueue<string>();
        var processed = new ConcurrentQueue<WatchFolderHistoryItem>();
        Task? retiredWorkers = null;
        var waitCallCount = 0;

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- kept adjacent to the call sites and captured state below.
        Task WaitForWorkers(Task workers, TimeSpan timeout)
        {
            // ReSharper disable once InvertIf -- the first-call special case reads better as the guard.
            if (Interlocked.Increment(ref waitCallCount) == 1)
            {
                retiredWorkers = workers;
                return Task.FromException(new TimeoutException("Simulated worker drain timeout."));
            }

            return workers.WaitAsync(timeout);
        }

        var service = new WatchFolderService(dataPath, WaitForWorkers);
        WatchFolderService.WatchFolderRun? oldRun = null;
        service.FileProcessed += (_, item) =>
        {
            processed.Enqueue(item);
            newProcessed.TrySetResult(item);
        };

        try
        {
            service.Start(
                CreateOptions(watchPath, outputPath, deleteSource: true),
                async (request, ct) =>
                {
                    oldCalls.Enqueue(Path.GetFileName(request.FilePath));
                    oldEntered.TrySetResult(ct);
                    await releaseOld.Task;
                    return CreateResult(request);
                }
            );

            var oldToken = await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            oldRun = service.CurrentRun;
            Assert.NotNull(oldRun);
            Assert.True(service.OwnsActiveFile(oldRun, sharedPath));
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));

            File.WriteAllBytes(newPath, [4, 5, 6]);
            service.Start(
                CreateOptions(watchPath, outputPath, deleteSource: true),
                async (request, ct) =>
                {
                    newCalls.Enqueue(Path.GetFileName(request.FilePath));
                    newEntered.TrySetResult(ct);
                    await releaseNew.Task;
                    return CreateResult(request);
                }
            );

            var newRun = service.CurrentRun;
            Assert.NotNull(newRun);
            var newToken = await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(["a-shared.wav"], oldCalls);
            Assert.Equal(["z-new.wav"], newCalls);
            Assert.DoesNotContain("a-shared.wav", newCalls);
            Assert.NotEqual(oldToken, newToken);
            Assert.True(oldToken.IsCancellationRequested);
            Assert.False(newToken.IsCancellationRequested);
            Assert.True(service.OwnsActiveFile(oldRun, sharedPath));
            Assert.True(service.OwnsActiveFile(newRun, newPath));

            releaseOld.TrySetResult(true);
            await retiredWorkers!.WaitAsync(TimeSpan.FromSeconds(15));
            await oldRun.RetiredCleanup.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("z-new.wav", service.CurrentlyProcessing);
            Assert.True(service.OwnsActiveFile(newRun, newPath));
            // The retired worker released its own reservation on unwind, so a later rescan of
            // the still-present source is no longer suppressed by a stale reservation.
            Assert.False(service.OwnsActiveFile(oldRun, sharedPath));
            Assert.True(File.Exists(sharedPath));
            Assert.False(File.Exists(Path.Join(outputPath, "a-shared.txt")));
            Assert.Empty(service.History);
            Assert.Empty(processed);
            Assert.Empty(oldRun.FailedFingerprints);
            Assert.False(File.Exists(Path.Join(dataPath, "watch-folder-processed.json")));

            releaseNew.TrySetResult(true);
            var item = await newProcessed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(item.Success, item.ErrorMessage);
            Assert.Equal("z-new.wav", item.FileName);
            Assert.Equal(Path.Join(outputPath, "z-new.txt"), item.OutputPath);
            Assert.True(File.Exists(item.OutputPath));
            Assert.False(File.Exists(newPath));
            Assert.True(File.Exists(sharedPath));
            Assert.Single(service.History);
            Assert.Single(processed);
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            releaseOld.TrySetResult(true);
            releaseNew.TrySetResult(true);
            var finalRun = service.CurrentRun;
            if (service.IsRunning)
            {
                await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (retiredWorkers is not null)
            {
                await retiredWorkers.WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (oldRun is not null)
            {
                await oldRun.RetiredCleanup.WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (finalRun is not null && !ReferenceEquals(finalRun, oldRun))
            {
                await finalRun.RetiredCleanup.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_UsesBoundedStopAndPreventsRestart()
    {
        var watchPath = Path.Join(_tempDir, "dispose-watch");
        var outputPath = Path.Join(_tempDir, "dispose-output");
        Directory.CreateDirectory(watchPath);
        Directory.CreateDirectory(outputPath);
        File.WriteAllBytes(Path.Join(watchPath, "blocked.wav"), [1, 2, 3]);

        var entered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Task? retiredWorkers = null;
        TimeSpan? requestedDeadline = null;

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- kept adjacent to the call sites and captured state below.
        Task WaitForWorkers(Task workers, TimeSpan timeout)
        {
            retiredWorkers = workers;
            requestedDeadline = timeout;
            return Task.FromException(new TimeoutException("Simulated worker drain timeout."));
        }

        var service = new WatchFolderService(
            Path.Join(_tempDir, "dispose-data"),
            WaitForWorkers
        );
        WatchFolderService.WatchFolderRun? oldRun = null;

        try
        {
            service.Start(
                CreateOptions(watchPath, outputPath),
                async (request, ct) =>
                {
                    entered.TrySetResult(ct);
                    await release.Task;
                    return CreateResult(request);
                }
            );

            var oldToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            oldRun = service.CurrentRun;
            Assert.NotNull(oldRun);
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(service.IsRunning);
            Assert.True(oldToken.IsCancellationRequested);
            Assert.Equal(TimeSpan.FromSeconds(2), requestedDeadline);
            Assert.Same(oldRun.WorkerCompletion, retiredWorkers);
            Assert.Throws<ObjectDisposedException>(
                () => service.Start(CreateOptions(watchPath, outputPath), TranscribeAsync)
            );

            service.Dispose();
            await service.DisposeAsync();
        }
        finally
        {
            release.TrySetResult(true);
            if (retiredWorkers is not null)
            {
                await retiredWorkers.WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (oldRun is not null)
            {
                await oldRun.RetiredCleanup.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await service.DisposeAsync();
        }
    }

    private static async Task<IReadOnlyList<WatchFolderHistoryItem>>
        StartAndWaitForProcessedItemsAsync(
            WatchFolderService service,
            int expectedCount,
            WatchFolderOptions options
        )
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var items = new ConcurrentQueue<WatchFolderHistoryItem>();

        void OnFileProcessed(object? sender, WatchFolderHistoryItem item)
        {
            items.Enqueue(item);
            if (items.Count >= expectedCount)
            {
                completion.TrySetResult(true);
            }
        }

        service.FileProcessed += OnFileProcessed;
        try
        {
            service.Start(options, TranscribeAsync);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return items.ToList();
        }
        finally
        {
            service.FileProcessed -= OnFileProcessed;
        }
    }

    private static Task<WatchFolderTranscriptionResult> TranscribeAsync(
        WatchFolderTranscriptionRequest request,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            new WatchFolderTranscriptionResult(
                $"Transcribed {Path.GetFileName(request.FilePath)}",
                "en",
                1,
                0.1,
                [],
                "fake",
                "test"
            )
        );
    }

    private static WatchFolderOptions CreateOptions(
        string watchPath,
        string outputPath,
        bool deleteSource = false
    )
    {
        return new WatchFolderOptions(
            watchPath,
            outputPath,
            WatchFolderOutputFormat.PlainText,
            deleteSource
        );
    }

    private static WatchFolderTranscriptionResult CreateResult(
        WatchFolderTranscriptionRequest request
    )
    {
        return new WatchFolderTranscriptionResult(
            $"Transcribed {Path.GetFileName(request.FilePath)}",
            "en",
            1,
            0.1,
            [],
            "fake",
            "test"
        );
    }
}
