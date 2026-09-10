using System.Net;
using System.Net.Http.Headers;

// This helper is file-linked into multiple plugin assemblies and the Core.Tests
// project. Its namespace must stay the canonical TypeWhisper.Plugins.Shared.Net that
// every consumer imports; matching any single link location (e.g. the Core.Tests
// link) would break the plugin builds. So the file-location check is a false positive.
// ReSharper disable once CheckNamespace
namespace TypeWhisper.Plugins.Shared.Net;

/// <summary>
///     Shared, resilient file downloader for the large on-demand GPU artifacts (CUDA
///     wheels, the sherpa GPU tarball, the whisper CUDA nupkg, the Parakeet/Canary
///     model files). It streams a remote object into a <em>stable</em>
///     <c>&lt;destination&gt;.partial</c> staging file with two properties the
///     per-call loops it replaces did not have:
///     <list type="number">
///         <item>
///             <description>
///                 <b>Range-based resume</b> — a dropped connection re-requests with
///                 <c>Range: bytes=N-</c> and appends to the surviving partial instead
///                 of restarting from zero. Only safe with a full-file integrity gate,
///                 so resume requires <c>verifyComplete</c> (see below).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Idle-read watchdog</b> — each <c>ReadAsync</c> is bounded by a
///                 short <c>idleTimeout</c>, so a half-open socket aborts
///                 in seconds (<see cref="DownloadStalledException" />) instead of
///                 hanging on the coarse <see cref="HttpClient.Timeout" /> ceiling.
///             </description>
///         </item>
///     </list>
///     <para>
///         Compiled into each plugin assembly via file-linking (like the shared CUDA
///         provisioner), so the type is <c>internal</c> — each copy stays private to
///         its assembly while still being reachable from that assembly's tests.
///     </para>
/// </summary>
internal static class ResilientDownloader
{
    private const int BufferSize = 81920;

    /// <summary>
    ///     Streams <paramref name="url" /> into <paramref name="destinationPath" />
    ///     via a stable <c>&lt;destinationPath&gt;.partial</c> staging file, with
    ///     Range-based resume (when <paramref name="allowResume" />) and an
    ///     idle-read watchdog, then atomically moves the verified partial into place.
    /// </summary>
    /// <param name="client">The HTTP client to use (carries the connect/total timeouts).</param>
    /// <param name="url">The artifact URL.</param>
    /// <param name="destinationPath">Final path; staging happens at this path + <c>.partial</c>.</param>
    /// <param name="approxTotalBytes">
    ///     A progress-denominator fallback for callers only; never consulted here and
    ///     never a completeness gate (the server's declared total drives that).
    /// </param>
    /// <param name="idleTimeout">Max time a single read may go without bytes before stalling out.</param>
    /// <param name="allowResume">
    ///     When true a surviving partial is resumed via <c>Range</c>; requires
    ///     <paramref name="verifyComplete" /> so a corrupt prefix can't re-append forever.
    /// </param>
    /// <param name="onBytesOnDisk">
    ///     Cumulative bytes-on-disk for this download (including any pre-existing
    ///     partial). Fired once at the start of streaming — so a resumed download's
    ///     progress jumps straight to its baseline — and again after every write.
    /// </param>
    /// <param name="verifyComplete">
    ///     Caller integrity check run on the completed partial <em>before</em> the
    ///     atomic move; it must throw on mismatch. Required when resuming, or when
    ///     the server omits an exact total (Content-Length/Content-Range).
    /// </param>
    /// <param name="ct">Cancellation for the whole operation (user cancel).</param>
    public static async Task DownloadToFileAsync(
        HttpClient client,
        string url,
        string destinationPath,
        // ReSharper disable once UnusedParameter.Global
        long? approxTotalBytes,
        TimeSpan idleTimeout,
        bool allowResume,
        Action<long>? onBytesOnDisk,
        Action<string>? verifyComplete,
        CancellationToken ct)
    {
        // Invariant: resume with no integrity gate is unrepresentable. Without a
        // full-file hash a corrupt/rotated prefix could be re-appended to forever.
        if (allowResume && verifyComplete is null)
            throw new ArgumentException(
                "allowResume requires a verifyComplete integrity check; resuming onto an "
                    + "unverified partial could re-append a corrupt prefix forever.",
                nameof(verifyComplete));

        // Resume needs a STABLE staging name so a surviving partial can be picked up next
        // time. Without resume there is nothing to pick up, and a stable name would let two
        // concurrent downloads of the same destination collide on one FileShare.None
        // partial — so use a per-call unique name there, matching the GUID-temp isolation
        // the non-resumable callers had before this helper.
        var partialPath = allowResume
            ? destinationPath + ".partial"
            : destinationPath + "." + Guid.NewGuid().ToString("N") + ".partial";

        try
        {
            // Only the 416 path ever re-requests: a partial that is >= the object
            // returns 416, we discard it, and the next attempt sends no Range (a clean
            // GET can't 416). Every other response is handled in a single pass.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var existing = allowResume && File.Exists(partialPath)
                    ? new FileInfo(partialPath).Length
                    : 0L;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0)
                    // RangeHeaderValue(N, null) => "bytes=N-": the next byte is index N,
                    // which equals the on-disk byte count. This arithmetic is exactly
                    // what the full-file hash later depends on.
                    request.Headers.Range = new RangeHeaderValue(existing, null);

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Partial already covers the whole object (complete or stale). Drop
                    // it and loop once more; the next attempt sends no Range.
                    TryDelete(partialPath);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var sentRange = existing > 0;
                var honoredRange =
                    sentRange && response.StatusCode == HttpStatusCode.PartialContent;

                long startOffset;
                FileMode mode;
                long? declaredTotal;

                if (honoredRange)
                {
                    // 206: append onto what we already have. The instance total comes
                    // from Content-Range ("bytes N-M/total").
                    startOffset = existing;
                    mode = FileMode.Append;
                    declaredTotal = response.Content.Headers.ContentRange?.Length;
                }
                else
                {
                    // 200: either we sent no Range, or the server ignored it / the
                    // object rotated, and it is re-sending the whole body. Truncate and
                    // restart from zero so we never interleave two different objects.
                    startOffset = 0;
                    mode = FileMode.Create;
                    declaredTotal = response.Content.Headers.ContentLength;
                }

                long onDisk;
                await using (var contentStream = await response.Content
                    .ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var fileStream = new FileStream(
                    partialPath, mode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    onDisk = startOffset;
                    // Baseline report: a resumed download jumps to its real position
                    // immediately, before the caller's progress throttle can swallow it.
                    onBytesOnDisk?.Invoke(onDisk);

                    var buffer = new byte[BufferSize];
                    while (true)
                    {
                        // Fresh idle deadline per read: if no bytes arrive within the
                        // window the linked CTS fires. A half-open socket aborts here
                        // in seconds instead of hanging on HttpClient.Timeout.
                        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idleCts.CancelAfter(idleTimeout);

                        int read;
                        try
                        {
                            read = await contentStream
                                .ReadAsync(buffer, idleCts.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // The idle timer fired, not the caller's token — classify as
                            // a stall, distinct from a user cancel (which rethrows below).
                            throw new DownloadStalledException(
                                $"Download stalled: no data received for "
                                    + $"{idleTimeout.TotalSeconds:0} s.");
                        }

                        if (read <= 0)
                            break;

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        onDisk += read;
                        onBytesOnDisk?.Invoke(onDisk);
                    }
                }

                // approxTotalBytes never gates completeness. Without a declared total, a
                // verifier is mandatory: clean EOF alone can't prove the object ended.
                if (declaredTotal is null && verifyComplete is null)
                    throw new DownloadIncompleteException(
                        "Download cannot be verified: the server did not declare an exact "
                            + "total length and the caller supplied no completion verifier."
                    );

                if (onDisk < declaredTotal)
                    throw new DownloadIncompleteException(
                        $"Download incomplete: wrote {onDisk} of {declaredTotal.Value} "
                            + "declared bytes before the stream ended.");

                break;
            }
        }
        catch
        {
            // Transient / idle / network / length failure. With resume, we KEEP the
            // partial so the next attempt picks up where this one stopped; without
            // resume a kept partial is useless, so drop it (truncate-restart parity).
            if (!allowResume)
                TryDelete(partialPath);
            throw;
        }

        // Integrity gate on the fully assembled partial. A mismatch (corruption, a
        // rotated upstream object, tampering) ALWAYS deletes the partial — resuming
        // onto it again would just re-confirm the same bad bytes — and restarts clean.
        if (verifyComplete is not null)
        {
            try
            {
                verifyComplete(partialPath);
            }
            catch
            {
                TryDelete(partialPath);
                throw;
            }
        }

        // Atomic publish: the destination only ever appears fully written and verified.
        File.Move(partialPath, destinationPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>
///     Thrown when a download makes no read progress within the idle window — a
///     half-open socket. Distinct from a user cancellation, which surfaces as an
///     <see cref="OperationCanceledException" />.
/// </summary>
internal sealed class DownloadStalledException(string message) : Exception(message);

/// <summary>
///     Thrown when the body ended before a server-declared total length
///     (Content-Length on a 200, Content-Range total on a 206), or when neither
///     a total nor a caller verifier can establish completeness.
/// </summary>
internal sealed class DownloadIncompleteException(string message) : Exception(message);
