using System.Net;
using System.Net.Http.Headers;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Core.Tests;

/// <summary>
///     The shared download free-space policy and its integration into
///     <see cref="ResilientDownloader" />. Both are file-linked into this project.
/// </summary>
public sealed class DownloadSpaceTests : IDisposable
{
    private const string Url = "https://example.test/artifact.bin";
    private const long Reserve = DownloadSpace.ReserveBytes;
    private readonly string _dir = Path.Join(
        Path.GetTempPath(),
        "tw-space-" + Guid.NewGuid().ToString("N")
    );

    public DownloadSpaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GetAvailableBytes_MissingTarget_UsesNearestExistingAncestor()
    {
        var missing = Path.Join(_dir, "not", "created", "yet");

        var available = DownloadSpace.GetAvailableBytes(missing);

        Assert.NotNull(available);
        Assert.InRange(
            available.Value,
            new DriveInfo(_dir).AvailableFreeSpace - 64L * 1024 * 1024,
            long.MaxValue
        );
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void GetAvailableBytes_SymlinkedDirectory_ReportsTheLinkTargetsFilesystem()
    {
        // /dev/shm is a separate tmpfs mount on Linux; skip where it is absent.
        if (!Directory.Exists("/dev/shm"))
            return;
        var target = Path.Join("/dev/shm", "tw-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            var link = Path.Join(_dir, "models");
            Directory.CreateSymbolicLink(link, target);

            var throughLink = DownloadSpace.GetAvailableBytes(Path.Join(link, "new-model"));
            var direct = new DriveInfo(target).AvailableFreeSpace;

            Assert.NotNull(throughLink);
            Assert.InRange(throughLink.Value, direct - 64L * 1024 * 1024, direct + 64L * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void GetAvailableBytes_PseudoFilesystem_IsUnknown()
    {
        if (!Directory.Exists("/proc/self"))
            return;

        Assert.Null(DownloadSpace.GetAvailableBytes("/proc/self"));
    }

    [Fact]
    public void FromCapacity_FullRealFilesystemIsZeroButNoCapacityIsUnknown()
    {
        Assert.Equal(0, DownloadSpace.FromCapacity(totalBytes: 1L << 40, availableBytes: 0));
        Assert.Equal(5, DownloadSpace.FromCapacity(totalBytes: 1L << 40, availableBytes: 5));
        Assert.Null(DownloadSpace.FromCapacity(totalBytes: 0, availableBytes: 0));
    }

    [Fact]
    public void EnsureAvailable_Shortfall_ReportsRequiredAvailableAndLocation()
    {
        const long required = 3L * 1024 * 1024 * 1024;
        const long available = 1L * 1024 * 1024 * 1024;

        var ex = Assert.Throws<InsufficientDownloadSpaceException>(
            () => DownloadSpace.EnsureAvailable(Path.Join(_dir, "model.bin"), required, "the test model", _ => available)
        );

        Assert.Equal(28, ex.HResult);
        Assert.Equal(required + Reserve, ex.RequiredBytes);
        Assert.Equal(available, ex.AvailableBytes);
        Assert.Equal(
            $"Not enough disk space for the test model: 3.3 GB must be free in {_dir} "
                + "(including a 256.0 MB safety margin), but only 1.0 GB is available. "
                + "Free up at least 2.3 GB and try again.",
            ex.Message
        );
    }

    [Fact]
    public void EnsureAvailable_ExactlyRequiredPlusReserve_Passes()
    {
        var ex = Record.Exception(
            () => DownloadSpace.EnsureAvailable(_dir, 1000, "x", _ => 1000 + Reserve)
        );
        Assert.Null(ex);
        Assert.Throws<InsufficientDownloadSpaceException>(
            () => DownloadSpace.EnsureAvailable(_dir, 1000, "x", _ => 999 + Reserve)
        );
    }

    [Fact]
    public void EnsureAvailable_NothingToWriteOrUnknownSpace_Passes()
    {
        Assert.Null(Record.Exception(() => DownloadSpace.EnsureAvailable(_dir, 0, "x", _ => 0)));
        Assert.Null(Record.Exception(() => DownloadSpace.EnsureAvailable(_dir, long.MaxValue / 2, "x", _ => null)));
    }

    [Fact]
    public void IsOutOfSpace_RecognizesFullDiskAndQuotaThroughWrappers()
    {
        var enospc = new IOException("No space left on device") { HResult = 28 };
        var edquot = new IOException("Disk quota exceeded") { HResult = 122 };

        Assert.True(DownloadSpace.IsOutOfSpace(enospc));
        Assert.True(DownloadSpace.IsOutOfSpace(edquot));
        Assert.True(DownloadSpace.IsOutOfSpace(new HttpRequestException("wrapped", enospc)));
        Assert.True(DownloadSpace.IsOutOfSpace(new AggregateException(new TimeoutException(), edquot)));
        Assert.False(DownloadSpace.IsOutOfSpace(new IOException("Permission denied") { HResult = 13 }));
        Assert.False(DownloadSpace.IsOutOfSpace(new InvalidDataException("bad archive")));
        Assert.False(DownloadSpace.IsOutOfSpace(null));
    }

    [Fact]
    public void TranslateWriteFailure_RealEnospc_BecomesSpaceError()
    {
        if (!File.Exists("/dev/full"))
            return;
        var write = Record.Exception(() =>
        {
            using var full = new FileStream("/dev/full", FileMode.Open, FileAccess.Write);
            full.Write(new byte[64 * 1024]);
            full.Flush();
        });

        var translated = DownloadSpace.TranslateWriteFailure(write!, Path.Join(_dir, "x.tmp"), "the test model");

        Assert.NotNull(translated);
        Assert.Same(write, translated.InnerException);
        Assert.Equal(
            $"Ran out of disk space while writing the test model to {_dir}. Free up space and try again.",
            translated.Message
        );
        Assert.Null(DownloadSpace.TranslateWriteFailure(new IOException("other"), _dir, "x"));
        var existing = new InsufficientDownloadSpaceException("x", _dir, 2, 1);
        Assert.Same(existing, DownloadSpace.TranslateWriteFailure(existing, _dir, "y"));
    }

    [Fact]
    public async Task Download_DeclaredLengthDoesNotFit_FailsBeforeWritingAnything()
    {
        var body = MakeBody(5000);
        var handler = new BodyHandler(body);
        using var client = new HttpClient(handler);
        var dest = Path.Join(_dir, "asset.bin");

        var ex = await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            Download(client, dest, allowResume: false, new DownloadSpaceRequirement("the asset", 0, _ => 4999 + Reserve))
        );

        Assert.Equal(5000 + Reserve, ex.RequiredBytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Download_Resume_RequiresOnlyTheRemainderAndKeepsThePartialOnShortfall()
    {
        var body = MakeBody(5000);
        var dest = Path.Join(_dir, "asset.bin");
        await File.WriteAllBytesAsync(dest + ".partial", body[..2000]);
        using var client = new HttpClient(new BodyHandler(body));

        await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            Download(client, dest, allowResume: true, new DownloadSpaceRequirement("the asset", 0, _ => 2999 + Reserve))
        );
        Assert.Equal(body[..2000], await File.ReadAllBytesAsync(dest + ".partial"));
        Assert.False(File.Exists(dest));

        await Download(client, dest, allowResume: true, new DownloadSpaceRequirement("the asset", 0, _ => 3000 + Reserve));
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Download_AdditionalBytesForExtraction_AreRequired()
    {
        var body = MakeBody(5000);
        var dest = Path.Join(_dir, "asset.bin");
        using var client = new HttpClient(new BodyHandler(body));

        var ex = await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            Download(client, dest, allowResume: false, new DownloadSpaceRequirement("the asset", 10_000, _ => 14_999 + Reserve))
        );

        Assert.Equal(15_000 + Reserve, ex.RequiredBytes);
    }

    [Fact]
    public async Task Download_UnknownLengthOrUnavailableProbe_Proceeds()
    {
        var body = MakeBody(5000);
        using var client = new HttpClient(new BodyHandler(body) { DeclareLength = false });
        var unknownLength = Path.Join(_dir, "unknown.bin");
        var unknownSpace = Path.Join(_dir, "probe.bin");

        await Download(client, unknownLength, allowResume: true, new DownloadSpaceRequirement("the asset", 0, _ => 0));
        await Download(client, unknownSpace, allowResume: true, new DownloadSpaceRequirement("the asset", 1L << 50, _ => null));

        Assert.Equal(body, await File.ReadAllBytesAsync(unknownLength));
        Assert.Equal(body, await File.ReadAllBytesAsync(unknownSpace));
    }

    [Fact]
    public async Task Download_DiskFillsDuringWrite_ReportsSpaceErrorAndPublishesNothing()
    {
        if (!File.Exists("/dev/full"))
            return;
        var body = MakeBody(200_000);
        var dest = Path.Join(_dir, "asset.bin");
        // The resume partial has a predictable name; /dev/full fails writes with a real ENOSPC.
        File.CreateSymbolicLink(dest + ".partial", "/dev/full");
        using var client = new HttpClient(new BodyHandler(body));

        var ex = await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            Download(client, dest, allowResume: true, new DownloadSpaceRequirement("the asset", 0, _ => null))
        );

        Assert.StartsWith("Ran out of disk space while writing the asset to ", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Download_WithoutRequirement_StillReportsAFullDisk()
    {
        if (!File.Exists("/dev/full"))
            return;
        var dest = Path.Join(_dir, "asset.bin");
        File.CreateSymbolicLink(dest + ".partial", "/dev/full");
        using var client = new HttpClient(new BodyHandler(MakeBody(200_000)));

        var ex = await Assert.ThrowsAsync<InsufficientDownloadSpaceException>(() =>
            Download(client, dest, allowResume: true, space: null)
        );

        Assert.Contains("asset.bin", ex.Message);
    }

    private static Task Download(
        HttpClient client,
        string dest,
        bool allowResume,
        DownloadSpaceRequirement? space
    ) =>
        ResilientDownloader.DownloadToFileAsync(
            client,
            Url,
            dest,
            approxTotalBytes: null,
            idleTimeout: TimeSpan.FromSeconds(30),
            allowResume,
            onBytesOnDisk: null,
            verifyComplete: allowResume ? _ => { } : null,
            CancellationToken.None,
            space
        );

    private static byte[] MakeBody(int length)
    {
        var body = new byte[length];
        for (var i = 0; i < length; i++)
            body[i] = (byte)(i % 251);
        return body;
    }

    private sealed class BodyHandler(byte[] body) : HttpMessageHandler
    {
        public bool DeclareLength { get; init; } = true;
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            var content = new ByteArrayContent(body[(int)from..]);
            content.Headers.ContentLength = DeclareLength ? body.Length - from : null;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = content,
            };
            if (from > 0)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            return Task.FromResult(response);
        }
    }
}
