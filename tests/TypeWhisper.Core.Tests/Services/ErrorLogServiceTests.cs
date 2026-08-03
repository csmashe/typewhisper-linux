using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="ErrorLogService" />'s contract that reporting an error never throws at the caller.</summary>
public sealed class ErrorLogServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.ErrorLogServiceTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    [Fact]
    public void AddEntry_WhenASubscriberThrows_StillRecordsAndDoesNotThrow()
    {
        var sut = new ErrorLogService(_tempDir);
        var secondSubscriberRan = false;
        sut.EntriesChanged += () => throw new InvalidOperationException("subscriber failed");
        sut.EntriesChanged += () => secondSubscriberRan = true;

        sut.AddEntry("something went wrong");

        // The throwing subscriber must not starve the one registered after it.
        Assert.True(secondSubscriberRan);
        Assert.Contains(sut.Entries, e => e.Message == "something went wrong");
    }

    [Fact]
    public void AddEntry_WhenASubscriberThrowsAnUnprintableException_DoesNotThrow()
    {
        var sut = new ErrorLogService(_tempDir);
        sut.EntriesChanged += () => throw new UnprintableException();

        // ToString is virtual, so formatting the failure must not become the failure.
        var thrown = Record.Exception(() => sut.AddEntry("something went wrong"));

        Assert.Null(thrown);
    }

    [Fact]
    public void ClearAll_WhenASubscriberThrows_DoesNotThrow()
    {
        var sut = new ErrorLogService(_tempDir);
        sut.AddEntry("something went wrong");
        sut.EntriesChanged += () => throw new InvalidOperationException("subscriber failed");

        var thrown = Record.Exception(sut.ClearAll);

        Assert.Null(thrown);
        Assert.Empty(sut.Entries);
    }

    private sealed class UnprintableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("cannot render");

        public override string Message => throw new InvalidOperationException("cannot render");
    }
}
