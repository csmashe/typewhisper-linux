using System.Runtime.Versioning;
using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

public sealed class AtomicFileWriteTests
{
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
