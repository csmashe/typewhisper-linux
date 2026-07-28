using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Core.Services;

public enum AtomicJsonBackupMode
{
    None,
    LastKnownGood,
}

public enum AtomicJsonCorruptFilePolicy
{
    Throw,
    PreserveAndReset,
}

public enum AtomicJsonStoreDiagnosticKind
{
    PrimaryCorrupt,
    BackupCorrupt,
    RecoveredFromBackup,
    CorruptFilePreserved,
}

public sealed record AtomicJsonStoreDiagnostic(
    AtomicJsonStoreDiagnosticKind Kind,
    string Path,
    string? PreservedPath,
    Exception? Exception
);

public sealed record AtomicJsonStoreOptions<T>
    where T : notnull
{
    public JsonSerializerOptions JsonOptions { get; init; } = new();
    public Func<string, T>? Deserialize { get; init; }
    public Func<T, string>? Serialize { get; init; }
    public AtomicJsonBackupMode BackupMode { get; init; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global -- store configuration API; every
    // in-tree caller takes the default "<primary>.bak" path, but an override must stay available.
    public string? BackupPath { get; init; }
    public AtomicJsonCorruptFilePolicy CorruptFilePolicy { get; init; }
    public Action<AtomicJsonStoreDiagnostic>? Diagnostic { get; init; }
}

/// <summary>
///     Serializes every store over one path. Deliberately non-generic: a per-type table would
///     hand two stores that disagree about <c>T</c> separate gates for the same file, which is
///     exactly the concurrent-write case the coordination exists to prevent.
/// </summary>
internal sealed class AtomicJsonPathCoordinator
{
    private static readonly ConcurrentDictionary<string, AtomicJsonPathCoordinator> s_coordinators =
        new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal
        );

    internal Lock Gate { get; } = new();

    internal long Revision { get; set; }

    internal static AtomicJsonPathCoordinator ForPath(string fullPath) =>
        s_coordinators.GetOrAdd(fullPath, static _ => new AtomicJsonPathCoordinator());
}

/// <summary>
///     Non-generic so the strict encoder is shared, rather than duplicated once per closed
///     <c>AtomicJsonStore&lt;T&gt;</c>.
/// </summary>
internal static class AtomicJsonText
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static string Decode(byte[] bytes, string path)
    {
        try
        {
            var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
            return s_strictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new JsonException($"'{path}' is not valid UTF-8 JSON.", ex);
        }
    }
}

/// <summary>
///     A process-local, per-path transaction coordinator for immutable JSON snapshots.
///     Atomic publication is delegated entirely to <see cref="AtomicFileWrite"/>.
/// </summary>
public sealed class AtomicJsonStore<T>
    where T : notnull
{
    private readonly Action<string, string> _atomicWrite;
    private readonly string _backupPath;
    private readonly AtomicJsonPathCoordinator _coordinator;
    private readonly Func<T> _createDefault;
    private readonly AtomicJsonStoreOptions<T> _options;

    private T _current = default!;
    private string? _currentJson;
    private bool _hasCommittedPrimary;
    private bool _loaded;
    private long _loadedRevision;

    public AtomicJsonStore(
        string path,
        Func<T> createDefault,
        AtomicJsonStoreOptions<T>? options = null
    )
        : this(
            path,
            createDefault,
            options ?? new AtomicJsonStoreOptions<T>(),
            AtomicFileWrite.WriteAllText
        )
    {
    }

    internal AtomicJsonStore(
        string path,
        Func<T> createDefault,
        AtomicJsonStoreOptions<T> options,
        Action<string, string> atomicWrite
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(createDefault);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(atomicWrite);

        FilePath = Path.GetFullPath(path);
        _createDefault = createDefault;
        _options = options;
        _atomicWrite = atomicWrite;
        _backupPath = Path.GetFullPath(options.BackupPath ?? FilePath + ".bak");
        _coordinator = AtomicJsonPathCoordinator.ForPath(FilePath);
    }

    // ReSharper disable once MemberCanBePrivate.Global -- store identity for callers that log or
    // reason about where a snapshot lives; not read in-tree, but part of the public surface.
    public string FilePath { get; }

    public T Current
    {
        get
        {
            lock (_coordinator.Gate)
            {
                EnsureFreshLocked();
                return _current;
            }
        }
    }

    public T Reload()
    {
        lock (_coordinator.Gate)
        {
            var loaded = LoadSnapshotLocked();
            PublishLoadedLocked(loaded);
            _coordinator.Revision++;
            _loadedRevision = _coordinator.Revision;
            return _current;
        }
    }

    public T Update(Func<T, T> update) => Update(update, out _);

    /// <param name="update">Produces the next snapshot from the current one.</param>
    /// <param name="changed">
    ///     Whether the stored value itself changed. False for a no-op update even when the
    ///     snapshot still had to be published to heal a missing or corrupt primary, so callers
    ///     can raise change events off this rather than off their own equality check.
    /// </param>
    public T Update(Func<T, T> update, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_coordinator.Gate)
        {
            EnsureFreshLocked();

            // Captured before the callback runs: an update is free to mutate the snapshot it
            // was handed and return it, which would leave nothing to compare the result against.
            var committedJson = CurrentJsonLocked();

            try
            {
                var candidate = update(_current)
                    ?? throw new InvalidOperationException("The JSON store update returned null.");

                // Serialization must finish before directory creation, backup publication, or
                // any other disk mutation. It also decides whether anything changed: object
                // equality would compare an in-place mutation against itself and drop it.
                var candidateJson = Serialize(candidate);
                changed = candidateJson != committedJson;

                // A no-op still publishes when no valid primary is committed, so that an
                // explicit save heals a missing or corrupt file instead of leaving it to be
                // preserved again on every later load.
                if (!changed && _hasCommittedPrimary)
                {
                    return _current;
                }

                EnsureParentDirectory();

                if (
                    _options.BackupMode == AtomicJsonBackupMode.LastKnownGood
                    && _hasCommittedPrimary
                )
                {
                    _atomicWrite(_backupPath, committedJson);
                }

                _atomicWrite(FilePath, candidateJson);

                _current = candidate;
                _currentJson = candidateJson;
                _hasCommittedPrimary = true;
                _loaded = true;
                _coordinator.Revision++;
                _loadedRevision = _coordinator.Revision;
                return _current;
            }
            catch
            {
                // A mutable snapshot may have been mutated in place before the failure. Drop the
                // cache only when it no longer matches what is committed, so the next read
                // reloads rather than exposing an update that never landed — while an immutable
                // snapshot, which cannot have diverged, keeps the instance it already handed out.
                if (!CacheStillMatchesLocked(committedJson))
                {
                    _loaded = false;
                }

                throw;
            }
        }
    }

    private void EnsureFreshLocked()
    {
        if (_loaded && _loadedRevision == _coordinator.Revision)
        {
            return;
        }

        var loaded = LoadSnapshotLocked();
        PublishLoadedLocked(loaded);
        _loadedRevision = _coordinator.Revision;
    }

    private LoadedSnapshot LoadSnapshotLocked()
    {
        if (TryReadBytes(FilePath, out var primaryBytes))
        {
            try
            {
                var primaryJson = AtomicJsonText.Decode(primaryBytes, FilePath);
                return new LoadedSnapshot(Deserialize(primaryJson), HasCommittedPrimary: true);
            }
            catch (JsonException ex)
            {
                Diagnose(
                    new AtomicJsonStoreDiagnostic(
                        AtomicJsonStoreDiagnosticKind.PrimaryCorrupt,
                        FilePath,
                        PreservedPath: null,
                        ex
                    )
                );

                return TryRecoverBackupLocked(out var recovered, out _)
                    ? recovered
                    : HandleCorruptPrimaryLocked(primaryBytes, ex);
            }
        }

        if (TryRecoverBackupLocked(out var backup, out var backupCorruption))
        {
            return backup;
        }

        if (
            backupCorruption is not null
            && _options.CorruptFilePolicy == AtomicJsonCorruptFilePolicy.Throw
        )
        {
            throw backupCorruption;
        }

        return new LoadedSnapshot(_createDefault(), HasCommittedPrimary: false);
    }

    private bool TryRecoverBackupLocked(
        out LoadedSnapshot recovered,
        out JsonException? corruption
    )
    {
        recovered = default;
        corruption = null;
        if (
            _options.BackupMode != AtomicJsonBackupMode.LastKnownGood
            || !TryReadBytes(_backupPath, out var backupBytes)
        )
        {
            return false;
        }

        T snapshot;
        string backupJson;
        try
        {
            backupJson = AtomicJsonText.Decode(backupBytes, _backupPath);
            snapshot = Deserialize(backupJson);
        }
        catch (JsonException ex)
        {
            corruption = ex;
            Diagnose(
                new AtomicJsonStoreDiagnostic(
                    AtomicJsonStoreDiagnosticKind.BackupCorrupt,
                    _backupPath,
                    PreservedPath: null,
                    ex
                )
            );
            if (_options.CorruptFilePolicy == AtomicJsonCorruptFilePolicy.PreserveAndReset)
            {
                PreserveCorruptFileLocked(_backupPath, backupBytes, ex);
            }

            return false;
        }

        var restored = TryRestorePrimaryLocked(backupJson);
        Diagnose(
            new AtomicJsonStoreDiagnostic(
                AtomicJsonStoreDiagnosticKind.RecoveredFromBackup,
                FilePath,
                PreservedPath: null,
                Exception: null
            )
        );
        recovered = new LoadedSnapshot(snapshot, HasCommittedPrimary: restored);
        return true;
    }

    /// <summary>
    ///     Rewriting the primary from a good backup is best effort: a read-only directory must
    ///     not turn a recoverable file into a failed load. When it does not stick the snapshot is
    ///     still returned, just not as a committed primary, so a later update republishes it.
    /// </summary>
    private bool TryRestorePrimaryLocked(string backupJson)
    {
        try
        {
            EnsureParentDirectory();
            _atomicWrite(FilePath, backupJson);
            _coordinator.Revision++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private LoadedSnapshot HandleCorruptPrimaryLocked(byte[] corruptBytes, JsonException exception)
    {
        if (_options.CorruptFilePolicy == AtomicJsonCorruptFilePolicy.Throw)
        {
            throw exception;
        }

        PreserveCorruptFileLocked(FilePath, corruptBytes, exception);
        return new LoadedSnapshot(_createDefault(), HasCommittedPrimary: false);
    }

    private void PreserveCorruptFileLocked(
        string path,
        byte[] corruptBytes,
        JsonException exception
    )
    {
        Diagnose(
            new AtomicJsonStoreDiagnostic(
                AtomicJsonStoreDiagnosticKind.CorruptFilePreserved,
                path,
                PreserveOnceLocked(path, corruptBytes),
                exception
            )
        );
    }

    /// <summary>
    ///     The corrupt original is deliberately left in place, so every later load runs into it
    ///     again. Copying it aside once per distinct content stops that from minting an endless
    ///     run of <c>.broken-*</c> files across reloads, peer stores, and restarts. Preserving
    ///     does not touch the primary, so peers have nothing to re-read and the revision stands.
    /// </summary>
    private static string PreserveOnceLocked(string path, byte[] corruptBytes) =>
        // Answered from disk every time rather than from a remembered path: a copy that has since
        // been deleted or moved must be written again, or an update could go on to overwrite the
        // primary with no preserved copy left anywhere.
        FindPreservedCopy(path, corruptBytes) ?? WriteNewPreservedCopy(path, corruptBytes);

    private static string WriteNewPreservedCopy(string path, byte[] corruptBytes)
    {
        var preservedPath =
            path
            + ".broken-"
            + DateTime.UtcNow.ToString("yyyyMMddHHmmss")
            + "-"
            + Guid.NewGuid().ToString("N");
        WriteCorruptCopy(path, preservedPath, corruptBytes);
        return preservedPath;
    }

    /// <summary>A copy this same content was already preserved into on an earlier run.</summary>
    private static string? FindPreservedCopy(string path, byte[] corruptBytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            foreach (
                var candidate in Directory.EnumerateFiles(
                    directory,
                    Path.GetFileName(path) + ".broken-*"
                )
            )
            {
                try
                {
                    if (
                        new FileInfo(candidate).Length == corruptBytes.Length
                        && File.ReadAllBytes(candidate).AsSpan().SequenceEqual(corruptBytes)
                    )
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable sibling just means this one is not the match.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a usable listing, fall back to writing a fresh copy.
        }

        return null;
    }

    /// <summary>
    ///     The preserved copy holds exactly the bytes of the original, so it has to hold the
    ///     original's permissions too. Plain create-new is umask-governed, which would widen an
    ///     owner-only settings, history, or secrets file to world-readable.
    /// </summary>
    private static void WriteCorruptCopy(
        string sourcePath,
        string preservedPath,
        byte[] corruptBytes
    )
    {
        if (OperatingSystem.IsWindows())
        {
            AtomicFileWrite.WriteAllBytesCreateNew(preservedPath, corruptBytes);
            return;
        }

        AtomicFileWrite.WriteAllBytesCreateNew(
            preservedPath,
            corruptBytes,
            ReadUnixModeOrOwnerOnly(sourcePath)
        );
    }

    [UnsupportedOSPlatform("windows")]
    private static UnixFileMode ReadUnixModeOrOwnerOnly(string path)
    {
        try
        {
            return File.GetUnixFileMode(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Owner-only is the safe answer when the original's mode cannot be read.
            return UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
    }

    private static bool TryReadBytes(string path, out byte[] bytes)
    {
        try
        {
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            bytes = [];
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            bytes = [];
            return false;
        }
    }

    private T Deserialize(string json)
    {
        try
        {
            var value = _options.Deserialize is not null
                ? _options.Deserialize(json)
                : JsonSerializer.Deserialize<T>(json, _options.JsonOptions);
            return value
                ?? throw new JsonException(
                    $"JSON in '{FilePath}' deserialized to null instead of {typeof(T).FullName}."
                );
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            throw new JsonException($"JSON in '{FilePath}' is invalid.", ex);
        }
    }

    private string Serialize(T value)
    {
        var json = _options.Serialize is not null
            ? _options.Serialize(value)
            : JsonSerializer.Serialize(value, _options.JsonOptions);
        return json
            ?? throw new InvalidOperationException(
                $"The serializer for {typeof(T).FullName} returned null."
            );
    }

    private void PublishLoadedLocked(LoadedSnapshot loaded)
    {
        _current = loaded.Value;
        _currentJson = null;
        _hasCommittedPrimary = loaded.HasCommittedPrimary;
        _loaded = true;
    }

    /// <summary>
    ///     The serialized form of the current snapshot, which is what a candidate is compared
    ///     against and what a last-known-good backup publishes. Computed on demand because a
    ///     store that is only ever read never needs it.
    /// </summary>
    private string CurrentJsonLocked() => _currentJson ??= Serialize(_current);

    private bool CacheStillMatchesLocked(string committedJson)
    {
        try
        {
            return Serialize(_current) == committedJson;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureParentDirectory()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void Diagnose(AtomicJsonStoreDiagnostic diagnostic)
    {
        try
        {
            _options.Diagnostic?.Invoke(diagnostic);
        }
        catch
        {
            // Diagnostics must not alter transaction or recovery semantics.
        }
    }

    private readonly record struct LoadedSnapshot(T Value, bool HasCommittedPrimary);
}
