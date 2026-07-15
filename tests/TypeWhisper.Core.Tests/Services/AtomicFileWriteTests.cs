using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class AtomicFileWriteTests
{
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
}

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
