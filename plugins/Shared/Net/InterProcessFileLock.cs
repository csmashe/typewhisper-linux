// This helper is file-linked into multiple plugin assemblies and the Core.Tests
// project. Its namespace must stay the canonical TypeWhisper.Plugins.Shared.Net that
// every consumer imports; matching any single link location (e.g. the Core.Tests
// link) would break the plugin builds. So the file-location check is a false positive.
// ReSharper disable once CheckNamespace
namespace TypeWhisper.Plugins.Shared.Net;

/// <summary>
///     A cross-process advisory lock built on an exclusively-opened sentinel file.
///     <para>
///         The on-demand GPU artifacts stage into <em>stable</em> paths in a SHARED
///         cache so a dropped download can resume (see <see cref="ResilientDownloader" />).
///         A stable path means two writers can pick the same file, and the per-engine
///         <c>SemaphoreSlim</c> gates only serialize within one provisioner instance —
///         not the two file-linked copies of the CUDA provisioner in different plugin
///         assemblies, and not two app processes sharing the cache. .NET honors
///         <see cref="FileShare" /> via <c>flock</c> on Unix, so an exclusive open of a
///         sentinel beside the staging file serializes all of those: a holder owns it
///         until it disposes the returned stream; everyone else polls until it frees.
///     </para>
///     <para>
///         Compiled into each plugin assembly via file-linking, so the type is
///         <c>internal</c> (like <see cref="ResilientDownloader" />).
///     </para>
/// </summary>
internal static class InterProcessFileLock
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Acquires the lock at <paramref name="lockPath" />, retrying until it is free
    ///     or <paramref name="ct" /> cancels. Dispose the returned stream to release.
    ///     The sentinel file is intentionally NEVER deleted: unlinking it on release
    ///     would let a later caller create a fresh inode and "acquire" the lock while a
    ///     current holder still owns the old one. It is an inert, empty file.
    /// </summary>
    public static async Task<FileStream> AcquireAsync(string lockPath, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            }
            catch (IOException)
            {
                // Another holder owns the lock; the work it guards (a large download +
                // extraction) is the long pole, so a coarse poll interval is fine.
                await Task.Delay(s_pollInterval, ct).ConfigureAwait(false);
            }
        }
    }
}
