using System.Runtime.Versioning;
using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

public sealed class AtomicFileWriteTests
{
    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllText_ReplaceOrdersModeThenFileSyncThenRenameThenDirectorySync()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteReplaceSyncOrderTests"
        );
        var path = Path.Join(directory, "state.json");
        var finalMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var calls = new List<string>();
        string? stagedPath = null;

        try
        {
            File.WriteAllText(path, "old");
            File.SetUnixFileMode(path, finalMode);

            // The hooks prove the publication order from observable filesystem state: the final
            // mode is present while the old destination remains at file-sync time, then the new
            // destination is present at directory-sync time.
            AtomicFileWrite.WriteAllText(
                path,
                "new",
                candidate => stagedPath = candidate,
                new AtomicFileWrite.SyncHooks(
                    (candidate, _) =>
                    {
                        calls.Add("file-sync");
                        Assert.Equal(stagedPath, candidate);
                        Assert.Equal("old", File.ReadAllText(path));
                        Assert.Equal("new", File.ReadAllText(candidate));
                        Assert.Equal(finalMode, File.GetUnixFileMode(candidate));
                    },
                    syncedDirectory =>
                    {
                        calls.Add("directory-sync");
                        Assert.Equal(directory, syncedDirectory);
                        Assert.Equal("new", File.ReadAllText(path));
                        Assert.Equal(finalMode, File.GetUnixFileMode(path));
                        Assert.NotNull(stagedPath);
                        Assert.False(File.Exists(stagedPath));
                    }
                )
            );

            Assert.Equal(["file-sync", "directory-sync"], calls);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllBytes_ReplaceReadOnlyDestination_PreservesModeAndSucceeds()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteReadOnlyReplaceTests"
        );
        var path = Path.Join(directory, "state.bin");
        byte[] replacement = [0, 1, 2, 255];
        var readOnlyMode =
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

        try
        {
            File.WriteAllBytes(path, [9, 8, 7, 6]);
            File.SetUnixFileMode(path, readOnlyMode);

            AtomicFileWrite.WriteAllBytes(path, replacement);

            Assert.Equal(replacement, File.ReadAllBytes(path));
            Assert.Equal(readOnlyMode, File.GetUnixFileMode(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllText_SymlinkThenDotDot_SyncsKernelResolvedParentPrefix()
    {
        var root = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteSymlinkParentTests"
        );
        var resolvedDirectory = Path.Join(root, "resolved");
        var symlinkTarget = Path.Join(resolvedDirectory, "nested");
        Directory.CreateDirectory(symlinkTarget);
        Directory.CreateSymbolicLink(Path.Join(resolvedDirectory, "link"), symlinkTarget);
        var rawParent = Path.Join(resolvedDirectory, "link", "..");
        var rawPath = Path.Join(rawParent, "state.json");
        var directorySyncObserved = false;

        try
        {
            AtomicFileWrite.WriteAllText(
                rawPath,
                "new",
                stagedWriteObserver: null,
                syncHooks: new AtomicFileWrite.SyncHooks(
                    (_, _) => { },
                    syncedDirectory =>
                    {
                        directorySyncObserved = true;
                        Assert.Equal(rawParent, syncedDirectory);
                    }
                )
            );

            Assert.True(directorySyncObserved);
            Assert.Equal("new", File.ReadAllText(Path.Join(resolvedDirectory, "state.json")));
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllBytesCreateNew_HardLinkSyncsDirectoryAfterLinkAndTempCleanup()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteHardLinkSyncOrderTests"
        );
        var path = Path.Join(directory, "data.bin");
        byte[] bytes = [0, 1, 2, 255];
        var calls = new List<string>();
        string? stagedPath = null;

        try
        {
            // At the hooks, path existence distinguishes pre-link file sync from the directory
            // sync that must follow both the link and removal of its temporary sibling name.
            AtomicFileWrite.WriteAllBytesCreateNew(
                path,
                bytes,
                attemptHardLink: true,
                stagedWriteObserver: candidate => stagedPath = candidate,
                syncHooks: new AtomicFileWrite.SyncHooks(
                    (candidate, _) =>
                    {
                        calls.Add("file-sync");
                        Assert.Equal(stagedPath, candidate);
                        Assert.False(File.Exists(path));
                        Assert.Equal(bytes, File.ReadAllBytes(candidate));
                    },
                    syncedDirectory =>
                    {
                        calls.Add("directory-sync");
                        Assert.Equal(directory, syncedDirectory);
                        Assert.Equal(bytes, File.ReadAllBytes(path));
                        Assert.NotNull(stagedPath);
                        Assert.False(File.Exists(stagedPath));
                    }
                )
            );

            Assert.Equal(["file-sync", "directory-sync"], calls);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllBytesCreateNew_RenameAt2FallbackSyncsDirectoryAfterRename()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteRenameAt2SyncOrderTests"
        );
        var path = Path.Join(directory, "data.bin");
        byte[] bytes = [255, 2, 1, 0];
        var calls = new List<string>();
        string? stagedPath = null;

        try
        {
            // Disabling hard links selects renameat2; the hooks prove its destination is absent
            // at file sync and visible, with the temp name consumed, at directory sync.
            AtomicFileWrite.WriteAllBytesCreateNew(
                path,
                bytes,
                attemptHardLink: false,
                stagedWriteObserver: candidate => stagedPath = candidate,
                syncHooks: new AtomicFileWrite.SyncHooks(
                    (candidate, _) =>
                    {
                        calls.Add("file-sync");
                        Assert.Equal(stagedPath, candidate);
                        Assert.False(File.Exists(path));
                        Assert.Equal(bytes, File.ReadAllBytes(candidate));
                    },
                    syncedDirectory =>
                    {
                        calls.Add("directory-sync");
                        Assert.Equal(directory, syncedDirectory);
                        Assert.Equal(bytes, File.ReadAllBytes(path));
                        Assert.NotNull(stagedPath);
                        Assert.False(File.Exists(stagedPath));
                    }
                )
            );

            Assert.Equal(["file-sync", "directory-sync"], calls);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllText_WhenDirectorySyncFails_ThrowsIndeterminateCommitException()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteDirectorySyncFailureTests"
        );
        var path = Path.Join(directory, "state.json");
        string? stagedPath = null;

        try
        {
            var error = Assert.Throws<AtomicFileWriteIndeterminateCommitException>(() =>
                AtomicFileWrite.WriteAllText(
                    path,
                    "new",
                    candidate => stagedPath = candidate,
                    new AtomicFileWrite.SyncHooks(
                        (_, _) => { },
                        _ => throw new InjectedDirectorySyncException()
                    )
                )
            );

            Assert.Contains("Indeterminate commit", error.Message, StringComparison.Ordinal);
            Assert.Contains(path, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("already exists", error.Message, StringComparison.Ordinal);
            Assert.IsType<InjectedDirectorySyncException>(error.InnerException);
            Assert.Equal("new", File.ReadAllText(path));
            Assert.NotNull(stagedPath);
            Assert.False(File.Exists(stagedPath));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void FlushDirectoryToDisk_RealLinuxDirectory_Succeeds()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteDirectorySyncSmokeTests"
        );

        try
        {
            // Ordering hooks cannot validate libc flags, signatures, or descriptor ownership;
            // this smoke exercises real open(2)/fsync(2). Actual power-loss durability is not
            // unit-testable and remains a filesystem/platform guarantee.
            AtomicFileWrite.FlushDirectoryToDisk(directory);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void FlushDirectoryToDisk_FilePath_Throws()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteDirectorySyncFilePathTests"
        );
        var path = Path.Join(directory, "not-a-directory");

        try
        {
            File.WriteAllText(path, "content");

            Assert.Throws<IOException>(() => AtomicFileWrite.FlushDirectoryToDisk(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllBytesCreateNew_WithUnixMode_PublishesCompleteOwnerOnlyFile()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteModeTests"
        );
        var path = Path.Join(directory, "secret-protection.key");
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        try
        {
            AtomicFileWrite.WriteAllBytesCreateNew(
                path,
                bytes,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );

            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path)
            );
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void WriteAllText_ReplacesDestinationCompletely()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-success-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "data.json");

        try
        {
            File.WriteAllText(path, "complete old content");

            AtomicFileWrite.WriteAllText(path, "complete new content");

            Assert.Equal("complete new content", File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WriteAllText_WhenTempWriteFails_LeavesDestinationUntouchedAndNoTempFile()
    {
        using var failurePath = new AtomicWriteFailureTestPath("complete old content");
        var before = File.ReadAllBytes(failurePath.FilePath);

        Assert.ThrowsAny<Exception>(() =>
            AtomicFileWrite.WriteAllText(failurePath.FilePath, "complete new content")
        );

        Assert.Equal(before, File.ReadAllBytes(failurePath.FilePath));
        Assert.Empty(failurePath.TemporaryFiles);
    }

    [Fact]
    public void WriteAllText_WhenStagedObserverFails_LeavesDestinationUntouchedAndCleansSibling()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteObserverTests"
        );
        var path = Path.Join(directory, "state.json");
        File.WriteAllText(path, "old");
        string? stagedPath = null;

        try
        {
            Assert.Throws<InjectedStagedWriteException>(() =>
                AtomicFileWrite.WriteAllText(
                    path,
                    "new",
                    candidate =>
                    {
                        stagedPath = candidate;
                        Assert.Equal("new", File.ReadAllText(candidate));
                        Assert.Equal(directory, Path.GetDirectoryName(candidate));
                        throw new InjectedStagedWriteException();
                    }
                )
            );

            Assert.Equal("old", File.ReadAllText(path));
            Assert.NotNull(stagedPath);
            Assert.False(File.Exists(stagedPath));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void WriteAllTextCreateNew_CreatesCompleteDestinationWithoutTempFile()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-create-new-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "data.json");

        try
        {
            AtomicFileWrite.WriteAllTextCreateNew(path, "complete new content");

            Assert.Equal("complete new content", File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WriteAllTextCreateNew_WhenDestinationExists_LeavesItUntouchedAndCleansTempFile()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-create-new-collision-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "data.json");

        try
        {
            File.WriteAllBytes(path, [0, 1, 2, 255]);
            var before = File.ReadAllBytes(path);

            Assert.Throws<IOException>(() =>
                AtomicFileWrite.WriteAllTextCreateNew(path, "replacement content")
            );

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WriteAllBytesCreateNew_ConcurrentlyToSamePath_OneWinnerPreservesItsBytes()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteConcurrentCreateNewTests"
        );
        var path = Path.Join(directory, "audio.wav");
        byte[][] candidates = [[0, 1, 2, 255], [255, 2, 1, 0], [7, 7, 7, 7], [3, 1, 4, 1]];
        using var start = new Barrier(candidates.Length);

        try
        {
            var writes = candidates
                .Select(
                    bytes =>
                        Task.Run(() =>
                        {
                            // ReSharper disable once AccessToDisposedClosure -- Task.WhenAll below
                            // awaits every task before `using var start` is disposed at scope end.
                            // Bounded: a thread-pool starvation stall would otherwise hang the
                            // whole run instead of failing this test.
                            Assert.True(
                                start.SignalAndWait(TimeSpan.FromSeconds(30)),
                                "The concurrent writers did not all reach the barrier."
                            );
                            try
                            {
                                AtomicFileWrite.WriteAllBytesCreateNew(
                                    path,
                                    bytes,
                                    attemptHardLink: false
                                );
                                // ReSharper disable once RedundantCast -- not redundant: the cast
                                // is what infers the tuple's Error element as nullable. Dropping
                                // it infers `Exception` and the compiler reports CS8619.
                                return (Bytes: bytes, Error: (Exception?)null);
                            }
                            catch (Exception ex)
                            {
                                return (Bytes: bytes, Error: ex);
                            }
                        })
                )
                .ToArray();

            var results = await Task.WhenAll(writes);

            var winner = Assert.Single(results, result => result.Error is null);
            var losers = results.Where(result => result.Error is not null).ToArray();
            Assert.NotEmpty(losers);
            foreach (var loser in losers)
            {
                Assert.IsType<IOException>(loser.Error);
                Assert.Contains("already exists", loser.Error!.Message, StringComparison.Ordinal);
            }

            Assert.Equal(winner.Bytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllBytesCreateNew_FallbackNeverReplacesPreExistingDestination()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteFallbackTests"
        );
        var path = Path.Join(directory, "data.json");
        byte[] foreignBytes = [0, 1, 2, 255];

        try
        {
            File.WriteAllBytes(path, foreignBytes);

            var error = Assert.Throws<IOException>(() =>
                AtomicFileWrite.WriteAllBytesCreateNew(
                    path,
                    [255, 2, 1, 0],
                    attemptHardLink: false
                )
            );

            Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
            Assert.Equal(foreignBytes, File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllBytesCreateNew_FallbackNeverReplacesDestinationRacedInAfterStaging()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteFallbackRaceTests"
        );
        var path = Path.Join(directory, "data.json");
        byte[] foreignBytes = [0, 1, 2, 255];
        var observed = false;

        try
        {
            var error = Assert.Throws<IOException>(() =>
                AtomicFileWrite.WriteAllBytesCreateNew(
                    path,
                    [255, 2, 1, 0],
                    attemptHardLink: false,
                    staged =>
                    {
                        observed = true;
                        Assert.False(File.Exists(path));
                        Assert.Equal(directory, Path.GetDirectoryName(staged));
                        File.WriteAllBytes(path, foreignBytes);
                    }
                )
            );

            Assert.True(observed);
            Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
            Assert.Equal(foreignBytes, File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    /// <summary>
    ///     Deleting the staged sibling via the observer is the only way to force a
    ///     non-<c>EEXIST</c> renameat2 failure, exercising the fail-closed path below.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("linux")]
    public void WriteAllBytesCreateNew_WhenAtomicPublishFailsWithoutCollision_FailsClosed()
    {
        var directory = TestPaths.CreateTempDirectory(
            "TypeWhisper.AtomicFileWriteFallbackFailClosedTests"
        );
        var path = Path.Join(directory, "data.json");
        byte[] foreignBytes = [0, 1, 2, 255];

        try
        {
            File.WriteAllBytes(path, foreignBytes);

            var error = Assert.Throws<IOException>(() =>
                AtomicFileWrite.WriteAllBytesCreateNew(
                    path,
                    [255, 2, 1, 0],
                    attemptHardLink: false,
                    File.Delete
                )
            );

            Assert.Contains(
                "without replacing an existing file",
                error.Message,
                StringComparison.Ordinal
            );
            Assert.Equal(foreignBytes, File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void WriteAllBytes_ReplacesDestinationCompletely()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-bytes-success-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "audio.wav");

        try
        {
            File.WriteAllBytes(path, [9, 8, 7, 6]);

            AtomicFileWrite.WriteAllBytes(path, [0, 1, 2, 255]);

            Assert.Equal([0, 1, 2, 255], File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WriteAllBytes_WhenTempWriteFails_LeavesDestinationUntouchedAndNoTempFile()
    {
        using var failurePath = new AtomicWriteFailureTestPath("complete old content");
        var before = File.ReadAllBytes(failurePath.FilePath);

        Assert.ThrowsAny<Exception>(() =>
            AtomicFileWrite.WriteAllBytes(failurePath.FilePath, [0, 1, 2, 255])
        );

        Assert.Equal(before, File.ReadAllBytes(failurePath.FilePath));
        Assert.Empty(failurePath.TemporaryFiles);
    }

    [Fact]
    public void WriteAllBytesCreateNew_CreatesCompleteDestinationWithoutTempFile()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-bytes-create-new-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "audio.wav");

        try
        {
            AtomicFileWrite.WriteAllBytesCreateNew(path, [0, 1, 2, 255]);

            Assert.Equal([0, 1, 2, 255], File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void WriteAllBytesCreateNew_WhenDestinationExists_LeavesItUntouchedAndCleansTempFile()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-bytes-create-new-collision-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "audio.wav");

        try
        {
            File.WriteAllBytes(path, [0, 1, 2, 255]);
            var before = File.ReadAllBytes(path);

            Assert.Throws<IOException>(() =>
                AtomicFileWrite.WriteAllBytesCreateNew(path, [255, 2, 1, 0])
            );

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

internal sealed class InjectedStagedWriteException : Exception;

internal sealed class InjectedDirectorySyncException : Exception;

internal sealed class AtomicWriteFailureTestPath : IDisposable
{
    public AtomicWriteFailureTestPath(string contents)
    {
        DirectoryPath = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-failure-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(DirectoryPath);

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
            Directory.Delete(DirectoryPath, true);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
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
