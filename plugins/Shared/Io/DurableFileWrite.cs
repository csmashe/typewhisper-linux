using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.Plugins.Shared.Io;

/// <summary>
///     Crash-durable full-file write: stages a unique temp sibling, fsyncs it, renames it
///     over the destination, then fsyncs the parent directory so the rename itself survives
///     power loss. The destination is only ever absent, its old content, or the complete new
///     content — never partial — and once this returns, the new content is on stable storage.
///     Failures throw, so a caller can refuse to take a dependent destructive step.
/// </summary>
// Local to the plugin compile set: TypeWhisper.Core's AtomicFileWrite is unreachable here
// (plugins reference only the PluginSDK and link shared sources file-by-file), and linking
// that file in would pull its LibraryImport marshalling (AllowUnsafeBlocks) and public Core
// types into every plugin assembly. DllImport matches CudaRuntimeProvisioner's interop style.
internal static class DurableFileWrite
{
    private const int OpenReadOnly = 0;

    // Linux asm-generic/fcntl.h defines O_DIRECTORY as octal 00200000 (0x10000);
    // x86-64 and arm64 share the value.
    private const int OpenDirectory = 0x10000;

    private static readonly SyncHooks s_productionSyncHooks = new(
        (_, stream) => stream.Flush(flushToDisk: true),
        FsyncDirectory
    );

    // Test seam: replaces the two durability syscalls so tests can pin the
    // stage -> fsync -> rename -> parent-fsync sequence without real power loss.
    internal sealed class SyncHooks(
        Action<string, FileStream> syncFile,
        Action<string> syncDirectory
    )
    {
        internal Action<string, FileStream> SyncFile { get; } = syncFile;
        internal Action<string> SyncDirectory { get; } = syncDirectory;
    }

    public static void WriteAllText(string path, string contents) =>
        WriteAllText(path, contents, syncHooks: null);

    internal static void WriteAllText(string path, string contents, SyncHooks? syncHooks)
    {
        syncHooks ??= s_productionSyncHooks;

        // Keep the raw parent prefix the temp-file and rename syscalls use. Lexical
        // normalization would resolve ".." before the kernel follows a preceding
        // symlink, which can select an unrelated directory for the fsync.
        var directoryPath = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directoryPath))
            directoryPath = ".";

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (
                var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            {
                stream.Write(Encoding.UTF8.GetBytes(contents));
                // Force the staged content out of the page cache BEFORE the rename, so a
                // crash can never publish a torn or empty destination.
                syncHooks.SyncFile(tempPath, stream);
            }

            // rename(2): the destination flips atomically from old (or absent) to complete.
            File.Move(tempPath, path, overwrite: true);

            // The rename is directory metadata; without this fsync a power loss can roll
            // the directory back to a state where the destination never appeared.
            syncHooks.SyncDirectory(directoryPath);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temp-file cleanup.
            }

            throw;
        }
    }

    private static void FsyncDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var directoryFileDescriptor = Open(directoryPath, OpenReadOnly | OpenDirectory);
        if (directoryFileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Could not open directory '{directoryPath}' for syncing (errno {error}).",
                new Win32Exception(error)
            );
        }

        try
        {
            if (Fsync(directoryFileDescriptor) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Could not sync directory '{directoryPath}' (errno {error}).",
                    new Win32Exception(error)
                );
            }
        }
        finally
        {
            // Linux close(2) may have closed the descriptor even when it reports an error,
            // so retrying could close an unrelated descriptor another thread just opened.
            _ = Close(directoryFileDescriptor);
        }
    }

    // Kept as DllImport for the same reason as CudaRuntimeProvisioner's: LibraryImport's
    // generated string marshalling would require AllowUnsafeBlocks in every consumer.
    // CharSet.Ansi marshals as UTF-8 on Linux — correct for these libc paths.
#pragma warning disable SYSLIB1054, CA2101
    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fileDescriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);
#pragma warning restore SYSLIB1054, CA2101
}
