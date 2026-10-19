using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services.UserData;

/// <summary>What a reset deletes beyond the user data it always deletes.</summary>
/// <param name="DeleteModels">Also delete downloaded models and GPU runtimes.</param>
public sealed record LocalDataResetOptions(bool DeleteModels);

internal enum StartupResetStatus
{
    /// <summary>No reset was requested.</summary>
    None,

    /// <summary>Everything the reset covers was deleted.</summary>
    Completed,

    /// <summary>Deleted, except the entries in <see cref="StartupResetResult.Remaining" />.</summary>
    Incomplete,

    /// <summary>Another startup holds the lock; nothing was deleted.</summary>
    LockUnavailable,

    /// <summary>The instance that requested the reset is still running; nothing was deleted.</summary>
    RequesterRunning,

    /// <summary>The data folder could not be opened safely; nothing was deleted.</summary>
    Failed,
}

/// <param name="Remaining">Paths relative to the data folder that could not be deleted, up to a limit.</param>
/// <param name="RemainingCount">How many entries could not be deleted in all.</param>
internal sealed record StartupResetResult(
    StartupResetStatus Status,
    IReadOnlyList<string> Remaining,
    int RemainingCount = 0,
    Exception? Error = null
);

/// <summary>
///     "Delete all data": a reset is requested while the app runs and carried out by the next
///     startup, before anything opens the data folder, so no work is in flight and nothing it
///     deletes is in use. The request is a marker in the data folder that is removed only once
///     the deletion has run, so a startup that is interrupted resumes it at the next launch.
/// </summary>
/// <remarks>
///     Only what TypeWhisper created is deleted (<see cref="LocalDataInventory" />): user data,
///     settings, stored secrets and the key that encrypts them, logs and internal files. Installed
///     plugins, the command line tool and the records of what TypeWhisper installed elsewhere
///     (autostart entry, shortcuts, launchers), GNOME shortcut backups and entries TypeWhisper did not
///     create stay. Models and GPU runtimes go only when asked; a custom model folder outside the
///     data folder is never touched. Links are deleted as links and never followed: every folder is
///     opened without following a link and emptied through that descriptor.
/// </remarks>
public sealed partial class LocalDataResetService
{
    internal const string PendingMarkerName = ".typewhisper-reset-pending.json";
    private const string ResultFileName = ".typewhisper-reset-result.json";
    private const string RestoreLockFileName = ".typewhisper-restore.lock";
    private const string ManagedArtifactsFolderName = "ManagedArtifacts";
    private const string OwnHiddenPrefix = ".typewhisper-";
    private const string StagedRestoreFolderName = ".typewhisper-restore-pending";
    private const string ApiDiscoveryFileName = "api-discovery.json";
    private const int MarkerVersion = 1;
    private const int MaxReportedPaths = 20;
    private const long MaxMarkerBytes = 64 * 1024;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _basePath;
    private readonly string? _apiDiscoveryDirectory;
    private readonly Action<string>? _beforeOpenFolder;
    private readonly TimeSpan _requesterWait;

    public LocalDataResetService()
        : this(TypeWhisperEnvironment.BasePath, ResolveApiDiscoveryDirectory()) { }

    internal LocalDataResetService(
        string basePath,
        string? apiDiscoveryDirectory = null,
        Action<string>? beforeOpenFolder = null,
        TimeSpan? requesterWait = null
    )
    {
        _basePath = Path.GetFullPath(basePath);
        _apiDiscoveryDirectory = apiDiscoveryDirectory;
        _beforeOpenFolder = beforeOpenFolder;
        _requesterWait = requesterWait ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Whether a reset is waiting for the next startup. A marker that cannot be inspected counts.</summary>
    public bool IsResetPending => EntryExists(Path.Join(_basePath, PendingMarkerName));

    /// <summary>
    ///     Records the reset for the next startup. Nothing is deleted yet; the caller restarts the app.
    /// </summary>
    public void RequestReset(LocalDataResetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Deleting all data requires Linux.");
        }

        Directory.CreateDirectory(_basePath);
        // Recorded now: settings.json may already be gone when an interrupted reset resumes.
        var modelFolders = ResolveModelFoldersInside(LocalDataExportService.ResolveLinks(_basePath));
        var marker = Path.Join(_basePath, PendingMarkerName);
        var temporary = Path.Join(_basePath, PendingMarkerName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew,
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                       }
                   ))
            {
                JsonSerializer.Serialize(
                    stream,
                    new PendingReset
                    {
                        Version = MarkerVersion,
                        RequestedUtc = DateTimeOffset.UtcNow,
                        DeleteModels = options.DeleteModels,
                        RequesterProcessId = Environment.ProcessId,
                        RequesterStartTime = ProcessStartTime(Environment.ProcessId),
                        ModelFolders = modelFolders,
                    },
                    s_jsonOptions
                );
                stream.Flush(flushToDisk: true);
            }

            // rename(2) replaces a link or file at the marker's name without writing through it.
            File.Move(temporary, marker, overwrite: true);
            Native.SyncDirectory(_basePath);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    /// <summary>
    ///     Carries out a requested reset. Runs at startup before the data folder is used, after the
    ///     single-instance check, under the lock that also guards a staged settings restore.
    /// </summary>
    internal static StartupResetResult ApplyPendingResetAtStartup(string basePath)
    {
        var service = new LocalDataResetService(basePath, ResolveApiDiscoveryDirectory());
        if (!service.IsResetPending)
        {
            return new StartupResetResult(StartupResetStatus.None, []);
        }

        FileStream startupLock;
        try
        {
            startupLock = SettingsBackupService.AcquireStartupRestoreLock(basePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StartupResetResult(StartupResetStatus.LockUnavailable, [], Error: ex);
        }

        using (startupLock)
        {
            return service.FinishPendingReset();
        }
    }

    /// <summary>Carries out a requested reset; the caller holds the startup lock.</summary>
    internal StartupResetResult FinishPendingReset()
    {
        if (!IsResetPending)
        {
            return new StartupResetResult(StartupResetStatus.None, []);
        }

        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc/self/fd"))
        {
            return Fail(new PlatformNotSupportedException("Deleting all data requires Linux with /proc."));
        }

        var root = LocalDataExportService.ResolveLinks(_basePath);
        var pending = ReadPending(Path.Join(root, PendingMarkerName));
        // Any launch can get here while the instance that asked is still closing and writing its data,
        // not only the one it restarted, and that instance does not hold the startup lock.
        if (!WaitForRequesterToExit(pending))
        {
            return new StartupResetResult(StartupResetStatus.RequesterRunning, []);
        }

        var options = new LocalDataResetOptions(pending?.DeleteModels ?? false);
        var modelFolders = (pending is null ? ResolveModelFoldersInside(root) : pending.ModelFolders ?? [])
            .Where(folder => folder.StartsWith(root + "/", StringComparison.Ordinal))
            .ToArray();

        ResetRun run;
        try
        {
            run = new ResetRun(root, options, modelFolders, _beforeOpenFolder);
            run.Execute();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return Fail(ex);
        }

        DeleteApiDiscoveryFile(run.Remaining);
        if (run.Remaining.Count > 0)
        {
            TryWriteResult(root, run.Remaining);
        }

        // Removed last, so an interrupted run resumes. Entries that cannot be deleted are reported
        // instead of deleting every new setting again at each launch.
        if (!run.DeleteMarker())
        {
            return Fail(new IOException("The reset request could not be removed."), run.Remaining);
        }

        return run.Remaining.Count == 0
            ? new StartupResetResult(StartupResetStatus.Completed, [])
            : new StartupResetResult(
                StartupResetStatus.Incomplete,
                run.Remaining.Take(MaxReportedPaths).ToArray(),
                run.Remaining.Count
            );

        StartupResetResult Fail(Exception error, IReadOnlyList<string>? remaining = null) =>
            new(
                StartupResetStatus.Failed,
                remaining?.Take(MaxReportedPaths).ToArray() ?? [],
                remaining?.Count ?? 0,
                error
            );
    }

    /// <summary>
    ///     What the last reset could not delete, once: the report is removed when read. Null when
    ///     the last reset deleted everything or none ran.
    /// </summary>
    public StartupResetResultReport? TakeIncompleteReport()
    {
        var path = Path.Join(_basePath, ResultFileName);
        if (!EntryExists(path))
        {
            return null;
        }

        try
        {
            using var source = SettingsBackupService.NativeFile.OpenRegularFile(path, ResultFileName, []);
            return source.Length > MaxMarkerBytes
                ? null
                : JsonSerializer.Deserialize<StartupResetResultReport>(source, s_jsonOptions);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    // A custom model folder inside the data folder. When it is one of TypeWhisper's own folders, only
    // its plugin assets and the shared GPU runtimes beside them count as models.
    private static string[] ResolveModelFoldersInside(string root) =>
        LocalDataExportService.ResolveCustomModelRootInside(root) is not { } custom
            ? []
            : Path.GetDirectoryName(custom) == root
              && LocalDataInventory.ClassifyTopLevel(Path.GetFileName(custom), isDirectory: true)
              != LocalDataCategory.Unrecognized
                ? [Path.Join(custom, LocalModelStoragePaths.PluginDataFolderName), Path.Join(custom, "Runtimes")]
                : [custom];

    private bool WaitForRequesterToExit(PendingReset? pending)
    {
        if (pending is not { RequesterProcessId: > 0 and var pid, RequesterStartTime: { } startTime }
            || pid == Environment.ProcessId)
        {
            return true;
        }

        var deadline = DateTime.UtcNow + _requesterWait;
        while (ProcessStartTime(pid) == startTime)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    // Field 22 of /proc/<pid>/stat; with the process ID it names one process even after the ID is reused.
    internal static string? ProcessStartTime(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields.Length > 19 ? fields[19] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static PendingReset? ReadPending(string marker)
    {
        try
        {
            using var source = SettingsBackupService.NativeFile.OpenRegularFile(marker, PendingMarkerName, []);
            if (source.Length <= MaxMarkerBytes
                && JsonSerializer.Deserialize<PendingReset>(source, s_jsonOptions) is { Version: MarkerVersion } pending)
            {
                return pending;
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or JsonException)
        {
            // An unreadable request still resets; it keeps models, the choice that deletes less.
        }

        return null;
    }

    [SupportedOSPlatform("linux")]
    private static void TryWriteResult(string root, List<string> remaining)
    {
        var path = Path.Join(root, ResultFileName);
        try
        {
            TryDeleteFile(path);
            using var stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }
            );
            JsonSerializer.Serialize(
                stream,
                new StartupResetResultReport(remaining.Count, remaining.Take(MaxReportedPaths).ToArray()),
                s_jsonOptions
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The startup result still reports it on the console and in the boot trace.
        }
    }

    // The API removes its discovery file when it stops; one left by a crash still holds the API token.
    private void DeleteApiDiscoveryFile(List<string> remaining)
    {
        if (_apiDiscoveryDirectory is null)
        {
            return;
        }

        foreach (var name in new[] { ApiDiscoveryFileName, ApiDiscoveryFileName + ".tmp" })
        {
            var path = Path.Join(_apiDiscoveryDirectory, name);
            try
            {
                if (SettingsBackupService.NativeFile.GetEntryKind(path)
                    is not SettingsBackupService.BackupEntryKind.Absent
                    and not SettingsBackupService.BackupEntryKind.Directory)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
            {
                remaining.Add(path);
            }
        }
    }

    private static string ResolveApiDiscoveryDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Join(configHome, "typewhisper");
    }

    private static bool EntryExists(string path)
    {
        try
        {
            return SettingsBackupService.NativeFile.GetEntryKind(path) != SettingsBackupService.BackupEntryKind.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private enum Decision
    {
        Delete,
        Keep,

        /// <summary>A folder whose contents are decided one by one; the folder itself stays.</summary>
        Partial,
    }

    /// <summary>One reset: the walk over the data folder and the entries it could not delete.</summary>
    [SupportedOSPlatform("linux")]
    private sealed class ResetRun(
        string root,
        LocalDataResetOptions options,
        string[] modelFolders,
        Action<string>? beforeOpenFolder
    )
    {
        // Folders a plugin keeps downloaded models and GPU runtimes in.
        private static readonly HashSet<string> s_pluginAssetFolders = new(StringComparer.Ordinal)
        {
            "Models",
            "model",
            "Runtimes",
        };

        private readonly string[] _modelFolders =
            modelFolders.Select(folder => Path.GetRelativePath(root, folder)).ToArray();

        private int _rootDescriptor = -1;

        public List<string> Remaining { get; } = [];

        public void Execute()
        {
            _rootDescriptor = Native.OpenDirectory(root);
            try
            {
                DiscardStagedRestore();
                EmptyFolder(_rootDescriptor, "", TopLevelDecision);
            }
            catch
            {
                Native.Close(_rootDescriptor);
                _rootDescriptor = -1;
                throw;
            }
        }

        public bool DeleteMarker()
        {
            try
            {
                return _rootDescriptor >= 0
                    && Native.Unlink(_rootDescriptor, PendingMarkerName) is Native.UnlinkResult.Deleted
                        or Native.UnlinkResult.Missing;
            }
            finally
            {
                if (_rootDescriptor >= 0)
                {
                    Native.Close(_rootDescriptor);
                    _rootDescriptor = -1;
                }
            }
        }

        // Renamed before anything is deleted, so a staged restore left half deleted is never taken for
        // a restore request; one that stays active would bring deleted settings back, so the reset fails.
        private void DiscardStagedRestore()
        {
            var discarded = OwnHiddenPrefix + "restore-discarded-" + Guid.NewGuid().ToString("N");
            if (Native.Rename(_rootDescriptor, StagedRestoreFolderName, discarded) is not (0 or Native.NoEntry) and var error)
            {
                throw new Win32Exception(error, "The staged settings restore could not be canceled.");
            }
        }

        private Decision TopLevelDecision(string name, bool isDirectory)
        {
            if (name is PendingMarkerName or RestoreLockFileName or ManagedArtifactsFolderName)
            {
                return Decision.Keep;
            }

            // Restore staging, a staged restore, reset temporaries and reports are TypeWhisper's own;
            // other hidden entries are not.
            if (name.StartsWith('.'))
            {
                return name.StartsWith(OwnHiddenPrefix, StringComparison.Ordinal) ? Decision.Delete : Decision.Keep;
            }

            var category = isDirectory
                ? LocalDataInventory.ClassifyTopLevel(name, isDirectory: true)
                : ClassifyFile(name);
            return category switch
            {
                LocalDataCategory.Settings
                    or LocalDataCategory.AppData
                    or LocalDataCategory.Recordings
                    or LocalDataCategory.Logs
                    or LocalDataCategory.Secrets
                    or LocalDataCategory.Internal => Decision.Delete,
                LocalDataCategory.PluginData => options.DeleteModels ? Decision.Delete : Decision.Partial,
                LocalDataCategory.Models or LocalDataCategory.Runtimes =>
                    options.DeleteModels ? Decision.Delete : Decision.Keep,
                _ => Decision.Keep,
            };
        }

        // A link at the top level is not followed, so it may name a folder entry or a file entry.
        private static LocalDataCategory ClassifyFile(string name)
        {
            var asFile = LocalDataInventory.ClassifyTopLevel(name, isDirectory: false);
            return asFile != LocalDataCategory.Unrecognized
                ? asFile
                : LocalDataInventory.ClassifyTopLevel(name, isDirectory: true);
        }

        // Keeping models: a plugin's model and runtime folders stay; its settings, collections,
        // scratch space and everything else go.
        private static Decision PluginDataDecision(string relativePath, bool isDirectory) =>
            relativePath.Count(c => c == '/') switch
            {
                1 => isDirectory ? Decision.Partial : Decision.Delete,
                _ => isDirectory && s_pluginAssetFolders.Contains(Path.GetFileName(relativePath))
                    ? Decision.Keep
                    : Decision.Delete,
            };

        private void EmptyFolder(int descriptor, string relativeFolder, Func<string, bool, Decision> decide)
        {
            string[] names;
            try
            {
                names = Native.List(descriptor);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Remaining.Add(relativeFolder + "/");
                return;
            }

            foreach (var name in names)
            {
                var relativePath = relativeFolder.Length == 0 ? name : relativeFolder + "/" + name;
                beforeOpenFolder?.Invoke(relativePath);
                var child = Native.OpenChildDirectory(descriptor, name, out var error);
                if (child >= 0)
                {
                    try
                    {
                        HandleFolder(descriptor, child, name, relativePath, decide);
                    }
                    finally
                    {
                        Native.Close(child);
                    }
                }
                else if (error is Native.NotDirectory or Native.Loop)
                {
                    // A file, link, socket or device: unlinked as itself, even under a Partial rule.
                    if (Override(relativePath) is null && decide(relativePath, false) != Decision.Keep)
                    {
                        DeleteEntry(descriptor, name, relativePath, isDirectory: false);
                    }
                }
                else if (error != Native.NoEntry)
                {
                    Remaining.Add(relativePath + "/");
                }
            }
        }

        private void HandleFolder(
            int parent,
            int descriptor,
            string name,
            string relativePath,
            Func<string, bool, Decision> decide
        )
        {
            var overridden = Override(relativePath);
            if (overridden == Decision.Keep)
            {
                return;
            }

            var decision = overridden == Decision.Delete ? Decision.Delete : decide(relativePath, true);
            var inner = decision switch
            {
                Decision.Delete => static (_, _) => Decision.Delete,
                Decision.Keep => static (_, _) => Decision.Keep,
                _ when !relativePath.Contains('/') => PluginDataDecision,
                _ => decide,
            };
            if (overridden == Decision.Partial || decision == Decision.Partial)
            {
                EmptyFolder(descriptor, relativePath, inner);
                return;
            }

            if (decision == Decision.Keep)
            {
                return;
            }

            var before = Remaining.Count;
            EmptyFolder(descriptor, relativePath, inner);
            if (Remaining.Count == before)
            {
                DeleteEntry(parent, name, relativePath, isDirectory: true);
            }
        }

        // Custom model folders inside the data folder are kept or deleted with models wherever they
        // lie, and every folder on the way to one is decided around it rather than as a whole.
        private Decision? Override(string relativePath)
        {
            if (_modelFolders.Contains(relativePath, StringComparer.Ordinal))
            {
                return options.DeleteModels ? Decision.Delete : Decision.Keep;
            }

            return _modelFolders.Any(folder => folder.StartsWith(relativePath + "/", StringComparison.Ordinal))
                ? Decision.Partial
                : null;
        }

        private void DeleteEntry(int parent, string name, string relativePath, bool isDirectory)
        {
            if (Native.Unlink(parent, name, isDirectory) == Native.UnlinkResult.Failed)
            {
                Remaining.Add(isDirectory ? relativePath + "/" : relativePath);
            }
        }
    }

    private sealed class PendingReset
    {
        public int Version { get; init; }
        public DateTimeOffset RequestedUtc { get; init; }
        public bool DeleteModels { get; init; }
        public int RequesterProcessId { get; init; }
        public string? RequesterStartTime { get; init; }
        public string[]? ModelFolders { get; init; }
    }

    [SupportedOSPlatform("linux")]
    private static partial class Native
    {
        public const int NoEntry = 2;
        public const int NotDirectory = 20;
        public const int Loop = 40;
        private const int OpenReadOnly = 0;
        private const int OpenDirectoryFlag = 0x10000;
        private const int OpenNoFollow = 0x20000;
        private const int OpenCloseOnExec = 0x80000;
        private const int AtRemoveDirectory = 0x200;

        public enum UnlinkResult
        {
            Deleted,
            Missing,
            Failed,
        }

        public static int OpenDirectory(string path)
        {
            var descriptor = open(path, OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec, 0);
            return descriptor >= 0
                ? descriptor
                : throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the data folder.");
        }

        public static int OpenChildDirectory(int parent, string name, out int error)
        {
            var descriptor = openat(parent, name, OpenReadOnly | OpenDirectoryFlag | OpenNoFollow | OpenCloseOnExec, 0);
            error = descriptor >= 0 ? 0 : Marshal.GetLastPInvokeError();
            return descriptor;
        }

        // The descriptor's /proc entry names the folder that was opened, even if its path now leads elsewhere.
        public static string[] List(int descriptor) =>
            Directory.GetFileSystemEntries($"/proc/self/fd/{descriptor}").Select(Path.GetFileName).OfType<string>().ToArray();

        public static UnlinkResult Unlink(int parent, string name, bool isDirectory = false)
        {
            if (unlinkat(parent, name, isDirectory ? AtRemoveDirectory : 0) == 0)
            {
                return UnlinkResult.Deleted;
            }

            var error = Marshal.GetLastPInvokeError();
            // EISDIR: the entry turned into a folder after it was looked at.
            if (error == 21 && !isDirectory && unlinkat(parent, name, AtRemoveDirectory) == 0)
            {
                return UnlinkResult.Deleted;
            }

            return error == NoEntry ? UnlinkResult.Missing : UnlinkResult.Failed;
        }

        /// <summary>Renames an entry within a folder; returns 0 or the error number.</summary>
        public static int Rename(int directory, string from, string to) =>
            renameat(directory, from, directory, to) == 0 ? 0 : Marshal.GetLastPInvokeError();

        public static void SyncDirectory(string path)
        {
            var descriptor = open(path, OpenReadOnly | OpenDirectoryFlag | OpenCloseOnExec, 0);
            if (descriptor < 0)
            {
                return;
            }

            _ = fsync(descriptor);
            Close(descriptor);
        }

        public static void Close(int descriptor) => _ = close(descriptor);

        // ReSharper disable InconsistentNaming -- native libc function names; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int open(string path, int flags, uint mode);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int openat(int directoryFileDescriptor, string path, int flags, uint mode);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int unlinkat(int directoryFileDescriptor, string path, int flags);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int renameat(int fromDirectory, string from, int toDirectory, string to);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int fsync(int fileDescriptor);

        [LibraryImport("libc", SetLastError = true)]
        private static partial int close(int fileDescriptor);
        // ReSharper restore InconsistentNaming
    }
}

/// <summary>What the last reset could not delete.</summary>
/// <param name="RemainingCount">How many entries were left in all.</param>
/// <param name="Remaining">Up to twenty of them, relative to the data folder; folders end in a slash.</param>
public sealed record StartupResetResultReport(int RemainingCount, IReadOnlyList<string> Remaining);
