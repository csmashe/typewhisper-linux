using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Core.Tests;

/// <summary>
///     Exercises the shared resumable/idle-watchdog downloader. The type is file-linked into
///     this project (see the .csproj), so it compiles as a single, unambiguous copy whose
///     internal surface is visible here without InternalsVisibleTo.
/// </summary>
public sealed class ResilientDownloaderTests
{
    private const string Url = "https://example.test/artifact.bin";
    private static readonly TimeSpan LongIdle = TimeSpan.FromSeconds(30);

    // Deterministic, position-dependent bytes so an interleaved/misaligned resume would
    // change the content and fail the verify.
    private static byte[] MakeBody(int length = 5000)
    {
        var body = new byte[length];
        for (var i = 0; i < length; i++)
            body[i] = (byte)(i % 251);
        return body;
    }

    // verifyComplete stand-in: passes only when the assembled file matches expected
    // exactly (the role the real callers' SHA-256 plays).
    private static Action<string> VerifyEquals(byte[] expected) => path =>
    {
        var actual = File.ReadAllBytes(path);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("Checksum mismatch.");
    };

    private static string NewTempDir()
    {
        var dir = Path.Join(Path.GetTempPath(), "tw-resilient-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task CleanDownload_WritesFinal_AndLeavesNoPartial()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            var handler = new ScriptedHandler(body);
            using var client = new HttpClient(handler);
            var dest = Path.Join(dir, "asset.bin");

            await ResilientDownloader.DownloadToFileAsync(
                client, Url, dest,
                approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                onBytesOnDisk: null, verifyComplete: VerifyEquals(body), ct: default);

            Assert.True(File.Exists(dest));
            Assert.Equal(body, await File.ReadAllBytesAsync(dest));
            Assert.False(File.Exists(dest + ".partial"));
            Assert.Equal(1, handler.RequestCount);
            Assert.Null(handler.ReceivedRanges[0]); // no Range sent on a fresh download
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Resume_FromSeededPartial_SendsRange_AndCompletes()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            const int n = 2000;
            var dest = Path.Join(dir, "asset.bin");
            await File.WriteAllBytesAsync(dest + ".partial", body[..n]);

            var handler = new ScriptedHandler(body);
            using var client = new HttpClient(handler);

            await ResilientDownloader.DownloadToFileAsync(
                client, Url, dest,
                approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                onBytesOnDisk: null, verifyComplete: VerifyEquals(body), ct: default);

            Assert.Equal(body, await File.ReadAllBytesAsync(dest));
            Assert.False(File.Exists(dest + ".partial"));
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal($"bytes={n}-", handler.ReceivedRanges[0]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ServerIgnoresRange_TruncatesAndRestarts_NoDoubleLength()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            const int n = 2000;
            var dest = Path.Join(dir, "asset.bin");
            // Seed with the wrong bytes too: a correct truncate-restart overwrites them.
            await File.WriteAllBytesAsync(dest + ".partial", new byte[n]);

            var handler = new ScriptedHandler(body) { HonorRange = false };
            using var client = new HttpClient(handler);

            await ResilientDownloader.DownloadToFileAsync(
                client, Url, dest,
                approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                onBytesOnDisk: null, verifyComplete: VerifyEquals(body), ct: default);

            var written = await File.ReadAllBytesAsync(dest);
            Assert.Equal(body.Length, written.Length); // not n + body.Length
            Assert.Equal(body, written);
            Assert.Equal($"bytes={n}-", handler.ReceivedRanges[0]); // it DID send a range
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RangeNotSatisfiable_DiscardsPartial_AndRefetchesClean()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            var dest = Path.Join(dir, "asset.bin");
            // Partial already covers the whole object → the ranged request 416s.
            await File.WriteAllBytesAsync(dest + ".partial", body);

            var handler = new ScriptedHandler(body);
            using var client = new HttpClient(handler);

            await ResilientDownloader.DownloadToFileAsync(
                client, Url, dest,
                approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                onBytesOnDisk: null, verifyComplete: VerifyEquals(body), ct: default);

            Assert.Equal(body, await File.ReadAllBytesAsync(dest));
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal($"bytes={body.Length}-", handler.ReceivedRanges[0]);
            Assert.Null(handler.ReceivedRanges[1]); // clean GET, no range
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task MidStreamDrop_RetainsPartial_AndSecondCallResumes()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            const int k = 2000;
            var dest = Path.Join(dir, "asset.bin");

            var handler = new ScriptedHandler(body)
            {
                WrapStream = slice => new FaultyStream(slice, k, FaultKind.Drop)
            };
            using var client = new HttpClient(handler);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                ResilientDownloader.DownloadToFileAsync(
                    client, Url, dest,
                    approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                    onBytesOnDisk: null, verifyComplete: VerifyEquals(body), ct: default));

            Assert.True(File.Exists(dest + ".partial"));
            Assert.Equal(k, new FileInfo(dest + ".partial").Length);

            // Second attempt: serve normally and resume from the retained partial.
            handler.WrapStream = null;
            await ResilientDownloader.DownloadToFileAsync(
                client, Url, dest,
                approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                onBytesOnDisk: null, verifyComplete: VerifyEquals(body), ct: default);

            Assert.Equal(body, await File.ReadAllBytesAsync(dest));
            Assert.False(File.Exists(dest + ".partial"));
            Assert.Equal($"bytes={k}-", handler.ReceivedRanges[^1]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task IdleStall_ThrowsStalled_AndRetainsPartial()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            const int k = 1000;
            var dest = Path.Join(dir, "asset.bin");

            var handler = new ScriptedHandler(body)
            {
                WrapStream = slice => new FaultyStream(slice, k, FaultKind.Stall)
            };
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<DownloadStalledException>(() =>
                ResilientDownloader.DownloadToFileAsync(
                    client, Url, dest,
                    approxTotalBytes: null, idleTimeout: TimeSpan.FromMilliseconds(50),
                    allowResume: true, onBytesOnDisk: null,
                    verifyComplete: VerifyEquals(body), ct: default));

            Assert.True(File.Exists(dest + ".partial"));
            Assert.Equal(k, new FileInfo(dest + ".partial").Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task UserCancellation_ThrowsCanceled_NotStalled()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            var dest = Path.Join(dir, "asset.bin");

            var handler = new ScriptedHandler(body)
            {
                WrapStream = slice => new FaultyStream(slice, 1000, FaultKind.Stall)
            };
            using var client = new HttpClient(handler);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(150));

            // A large idle timeout ensures the watchdog does not fire before the cancel.
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ResilientDownloader.DownloadToFileAsync(
                    client, Url, dest,
                    approxTotalBytes: null, idleTimeout: LongIdle, allowResume: false,
                    onBytesOnDisk: null, verifyComplete: null, ct: cts.Token));

            Assert.IsNotType<DownloadStalledException>(ex);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task TruncatedBody_ThrowsIncomplete_AndKeepsPartial()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            const int k = 2000;
            var dest = Path.Join(dir, "asset.bin");

            // Declares the full slice length but serves only k bytes then clean-EOFs.
            var handler = new ScriptedHandler(body)
            {
                WrapStream = slice => new FaultyStream(slice, k, FaultKind.Truncate)
            };
            using var client = new HttpClient(handler);

            await Assert.ThrowsAsync<DownloadIncompleteException>(() =>
                ResilientDownloader.DownloadToFileAsync(
                    client, Url, dest,
                    approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                    onBytesOnDisk: null, verifyComplete: VerifyEquals(body), ct: default));

            Assert.True(File.Exists(dest + ".partial"));
            Assert.Equal(k, new FileInfo(dest + ".partial").Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ChecksumMismatch_Propagates_AndDeletesPartial()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            var dest = Path.Join(dir, "asset.bin");

            var handler = new ScriptedHandler(body);
            using var client = new HttpClient(handler);

            Action<string> alwaysFails = _ => throw new InvalidOperationException("Checksum mismatch.");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ResilientDownloader.DownloadToFileAsync(
                    client, Url, dest,
                    approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                    onBytesOnDisk: null, verifyComplete: alwaysFails, ct: default));

            Assert.False(File.Exists(dest + ".partial")); // corrupt → restart clean
            Assert.False(File.Exists(dest));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ResumeWithoutVerify_ThrowsArgumentException()
    {
        var dir = NewTempDir();
        try
        {
            using var client = new HttpClient(new ScriptedHandler(MakeBody()));
            var dest = Path.Join(dir, "asset.bin");

            await Assert.ThrowsAsync<ArgumentException>(() =>
                ResilientDownloader.DownloadToFileAsync(
                    client, Url, dest,
                    approxTotalBytes: null, idleTimeout: LongIdle, allowResume: true,
                    onBytesOnDisk: null, verifyComplete: null, ct: default));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task NonResumableFailure_DeletesPartial()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            var dest = Path.Join(dir, "asset.bin");

            var handler = new ScriptedHandler(body)
            {
                WrapStream = slice => new FaultyStream(slice, 2000, FaultKind.Drop)
            };
            using var client = new HttpClient(handler);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                ResilientDownloader.DownloadToFileAsync(
                    client, Url, dest,
                    approxTotalBytes: null, idleTimeout: LongIdle, allowResume: false,
                    onBytesOnDisk: null, verifyComplete: null, ct: default));

            // No resume → the (uniquely named) partial is dropped on failure.
            Assert.Empty(Directory.GetFiles(dir, "*.partial"));
            Assert.False(File.Exists(dest));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Regression: the non-resume path uses a unique partial name, so concurrent
    // same-destination downloads don't collide on one FileShare.None partial.
    [Fact]
    public async Task ConcurrentNonResumableDownloads_DoNotCollide()
    {
        var dir = NewTempDir();
        try
        {
            var body = MakeBody();
            var dest = Path.Join(dir, "asset.bin");
            using var client = new HttpClient(new ScriptedHandler(body));

            Task Download() => ResilientDownloader.DownloadToFileAsync(
                client, Url, dest,
                approxTotalBytes: null, idleTimeout: LongIdle, allowResume: false,
                onBytesOnDisk: null, verifyComplete: null, ct: default);

            await Task.WhenAll(Download(), Download(), Download());

            Assert.Equal(body, await File.ReadAllBytesAsync(dest));
            Assert.Empty(Directory.GetFiles(dir, "*.partial")); // all unique partials moved away
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Test doubles -------------------------------------------------------

    // Serves a fixed body with real Range semantics (206/200/416) and lets a test wrap
    // the served slice in a FaultyStream to inject drops/stalls/truncation. Extends the
    // CapturingHandler pattern used elsewhere in the suite.
    private sealed class ScriptedHandler(byte[] body) : HttpMessageHandler
    {
        public bool HonorRange { get; set; } = true;
        public Func<byte[], Stream>? WrapStream { get; set; }
        public int RequestCount { get; private set; }
        public List<string?> ReceivedRanges { get; } = [];

        // The concurrent test (12) drives one handler from several SendAsync calls at
        // once, so guard the shared bookkeeping to keep it deterministic.
        private readonly object _sync = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range;
            lock (_sync)
            {
                RequestCount++;
                ReceivedRanges.Add(range?.ToString());
            }

            long from = 0;
            var hasRange = HonorRange && range is not null && range.Ranges.Count == 1;
            if (hasRange)
                from = range!.Ranges.First().From ?? 0;

            // The requested start is at/after the end of the object → 416.
            if (hasRange && from >= body.Length)
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            byte[] slice;
            HttpStatusCode status;
            ContentRangeHeaderValue? contentRange = null;
            if (hasRange && from > 0)
            {
                slice = body[(int)from..];
                status = HttpStatusCode.PartialContent;
                contentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            }
            else
            {
                slice = body;
                status = HttpStatusCode.OK;
            }

            Stream stream = WrapStream is not null
                ? WrapStream(slice)
                : new MemoryStream(slice, writable: false);
            var content = new StreamContent(stream);
            // Declare the FULL slice length even when a WrapStream serves fewer bytes, so
            // a truncated body trips the completeness check.
            content.Headers.ContentLength = slice.Length;
            if (contentRange is not null)
                content.Headers.ContentRange = contentRange;

            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private enum FaultKind { Drop, Stall, Truncate }

    // Serves `serveBytes` of `data`, then faults: throws (Drop), hangs honoring the
    // cancellation token (Stall), or clean-EOFs early (Truncate).
    private sealed class FaultyStream(byte[] data, int serveBytes, FaultKind kind) : Stream
    {
        private int _pos;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos < serveBytes)
            {
                var n = Math.Min(buffer.Length, serveBytes - _pos);
                data.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }

            switch (kind)
            {
                case FaultKind.Drop:
                    throw new IOException("simulated mid-stream connection drop");
                case FaultKind.Stall:
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return 0; // unreachable
                default: // Truncate: end the body before the declared length.
                    return 0;
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
