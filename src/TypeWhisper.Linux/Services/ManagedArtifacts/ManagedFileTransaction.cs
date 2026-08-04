using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services.ManagedArtifacts;

/// <summary>
///     Journaled ownership transaction for installer-controlled whole files.
///     It deliberately does not support edits inside user-owned container files.
/// </summary>
public sealed partial class ManagedFileTransaction
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const string StateFileName = "state.json";
    private const string PublishedImageName = "published.bin";
    private const string PreimageName = "preimage.bin";
    private const string JournalFileName = "pending.json";
    private const string JournalOldImageName = "pending-old.bin";
    private const string JournalNewImageName = "pending-new.bin";
    private const string JournalPreimageName = "pending-preimage.bin";
    private const string LockFileName = "transaction.lock";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_processLocks = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal
    );

    private readonly string _stateRoot;
    private readonly Func<ManagedFileCheckpoint, CancellationToken, Task>? _checkpoint;

    /// <summary>Where installed artifacts are recorded outside tests.</summary>
    public static string DefaultStateRoot =>
        Path.Join(TypeWhisperEnvironment.BasePath, "ManagedArtifacts");

    public ManagedFileTransaction()
        : this(DefaultStateRoot) { }

    public ManagedFileTransaction(string stateRoot)
        : this(stateRoot, checkpoint: null) { }

    internal ManagedFileTransaction(
        string stateRoot,
        Func<ManagedFileCheckpoint, CancellationToken, Task>? checkpoint
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        _checkpoint = checkpoint;
    }

    public ManagedFileClassification Probe(ManagedFileSpec spec)
    {
        return ProbeAsync(spec, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<ManagedFileClassification> ProbeAsync(
        ManagedFileSpec spec,
        CancellationToken ct
    )
    {
        ValidateSpec(spec);
        var existingPaths = new ArtifactPaths(Path.Join(_stateRoot, spec.ArtifactId));
        if (NativeFile.GetEntryKind(existingPaths.Directory) == EntryKind.Absent)
        {
            var unrecorded = await CaptureEntryAsync(spec.DestinationPath, ct)
                .ConfigureAwait(false);
            return await ClassifyAsync(
                    spec,
                    existingPaths,
                    state: null,
                    entry: unrecorded,
                    ct: ct
                )
                .ConfigureAwait(false);
        }

        var paths = PrepareStatePaths(spec.ArtifactId);
        await using var artifactLock = await AcquireLockAsync(paths, ct).ConfigureAwait(false);
        await RecoverAsync(spec, paths, ct).ConfigureAwait(false);
        var state = await TryReadStateAsync(spec, paths, ct).ConfigureAwait(false);
        var entry = await CaptureEntryAsync(spec.DestinationPath, ct).ConfigureAwait(false);
        return await ClassifyAsync(spec, paths, state, entry, ct).ConfigureAwait(false);
    }

    public async Task<ManagedFileOperationResult> InstallAsync(
        ManagedFileSpec spec,
        CancellationToken ct = default
    )
    {
        ValidateSpec(spec);
        var paths = PrepareStatePaths(spec.ArtifactId);
        await using var artifactLock = await AcquireLockAsync(paths, ct).ConfigureAwait(false);
        await RecoverAsync(spec, paths, ct).ConfigureAwait(false);

        var state = await TryReadStateAsync(spec, paths, ct).ConfigureAwait(false);
        var captured = await CaptureEntryAsync(spec.DestinationPath, ct).ConfigureAwait(false);
        var classification = await ClassifyAsync(spec, paths, state, captured, ct)
            .ConfigureAwait(false);

        // ReSharper disable once ConvertIfStatementToSwitchStatement -- the following guards test
        // classification together with spec.ExistingPolicy, so a switch would need `when` clauses
        // and read worse than this straight guard chain.
        if (classification == ManagedFileClassification.CurrentOwned)
        {
            if (state is null)
            {
                await AdoptCurrentAsync(spec, paths, captured, ct).ConfigureAwait(false);
            }

            return new ManagedFileOperationResult(classification, false, true);
        }

        if (
            classification == ManagedFileClassification.EquivalentForeign
            && spec.ExistingPolicy == ManagedFileExistingPolicy.AcceptEquivalentWithoutOwning
        )
        {
            return new ManagedFileOperationResult(classification, false, false);
        }

        var mayPublish = classification is ManagedFileClassification.Absent
            or ManagedFileClassification.StaleOwned;
        var backingUpForeign =
            classification == ManagedFileClassification.Foreign
            && spec.ExistingPolicy == ManagedFileExistingPolicy.BackupTransformAndRestore;
        if (!mayPublish && !backingUpForeign)
        {
            return Refused(classification);
        }

        byte[] desired;
        if (
            spec.ExistingPolicy == ManagedFileExistingPolicy.BackupTransformAndRestore
            && classification
                is ManagedFileClassification.Foreign
                    or ManagedFileClassification.StaleOwned
        )
        {
            if (spec.BackupTransform is null)
            {
                throw new InvalidOperationException(
                    $"Artifact '{spec.ArtifactId}' requires a backup transform."
                );
            }

            byte[] transformSource;
            if (backingUpForeign)
            {
                transformSource = captured.Bytes
                    ?? throw new InvalidOperationException("Foreign preimage is unavailable.");
            }
            else
            {
                transformSource = (
                    await ReadValidPreimageAsync(spec, paths, state, ct).ConfigureAwait(false)
                )?.Bytes ?? throw new InvalidDataException(
                    $"Artifact '{spec.ArtifactId}' has no recorded transform preimage."
                );
            }

            desired = spec.BackupTransform(transformSource);
        }
        else
        {
            desired = spec.DesiredBytes.ToArray();
        }

        var destinationDirectory = Path.GetDirectoryName(spec.DestinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        var stage = await StageAsync(
                spec.DestinationPath,
                desired,
                spec.CreateMode,
                ct
            )
            .ConfigureAwait(false);

        // Any journal written below is intentionally left behind if this block throws, so a
        // later run can replay it for exact recovery.
        try
        {
            if (spec.StagedFileValidator is not null)
            {
                await spec.StagedFileValidator(stage, ct).ConfigureAwait(false);
            }

            await CheckpointAsync(ManagedFileCheckpoint.InstallAfterStage, ct)
                .ConfigureAwait(false);
            var existingPreimage = await ReadValidPreimageAsync(spec, paths, state, ct)
                .ConfigureAwait(false);
            var preimageBytes = existingPreimage?.Bytes;
            var preimageMode = existingPreimage?.Mode;
            var journalCarriesPreimage = false;
            if (backingUpForeign)
            {
                preimageBytes = captured.Bytes!.ToArray();
                preimageMode = captured.Mode;
                journalCarriesPreimage = true;
            }

            var journal = new ManagedFileJournal
            {
                Operation = ManagedFileJournalOperation.Install,
                ArtifactId = spec.ArtifactId,
                DestinationPath = spec.DestinationPath,
                OldExists = captured.Kind == EntryKind.Regular,
                OldMode = captured.Kind == EntryKind.Regular ? (int)captured.Mode : null,
                NewExists = true,
                NewMode = (int)spec.CreateMode,
                FinalPreimageSha256 = preimageBytes is null ? null : Sha256(preimageBytes),
                FinalPreimageMode = preimageBytes is null ? null : (int?)preimageMode,
                JournalCarriesPreimage = journalCarriesPreimage,
            };
            await WriteJournalAsync(
                    paths,
                    journal,
                    captured.Bytes,
                    desired,
                    journalCarriesPreimage ? preimageBytes : null,
                    ct
                )
                .ConfigureAwait(false);
            await CheckpointAsync(ManagedFileCheckpoint.InstallAfterJournal, ct)
                .ConfigureAwait(false);

            var current = await CaptureEntryAsync(spec.DestinationPath, ct).ConfigureAwait(false);
            if (!EntryEquals(captured, current))
            {
                throw new ManagedFileConcurrencyException(
                    $"'{spec.DestinationPath}' changed while '{spec.ArtifactId}' was being installed."
                );
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stage, spec.DestinationPath, true);
            stage = string.Empty;
            await CheckpointAsync(ManagedFileCheckpoint.InstallAfterPublish, ct)
                .ConfigureAwait(false);

            await FinalizeInstallAsync(spec, paths, journal, ct).ConfigureAwait(false);
            await CheckpointAsync(ManagedFileCheckpoint.InstallAfterState, ct)
                .ConfigureAwait(false);
            DeleteJournal(paths);
        }
        finally
        {
            DeleteBestEffort(stage);
        }

        if (spec.PostCommit is not null)
        {
            await spec.PostCommit(ct).ConfigureAwait(false);
        }

        return new ManagedFileOperationResult(classification, true, true);
    }

    public async Task<ManagedFileOperationResult> RemoveAsync(
        ManagedFileSpec spec,
        CancellationToken ct = default
    )
    {
        ValidateSpec(spec);
        var paths = PrepareStatePaths(spec.ArtifactId);
        await using var artifactLock = await AcquireLockAsync(paths, ct).ConfigureAwait(false);
        await RecoverAsync(spec, paths, ct).ConfigureAwait(false);

        var state = await TryReadStateAsync(spec, paths, ct).ConfigureAwait(false);
        var captured = await CaptureEntryAsync(spec.DestinationPath, ct).ConfigureAwait(false);
        var classification = await ClassifyAsync(spec, paths, state, captured, ct)
            .ConfigureAwait(false);
        if (
            state is null
            && classification
                is ManagedFileClassification.CurrentOwned
                    or ManagedFileClassification.StaleOwned
        )
        {
            await AdoptCurrentAsync(spec, paths, captured, ct).ConfigureAwait(false);
            state = await TryReadStateAsync(spec, paths, ct).ConfigureAwait(false);
        }

        if (state is null)
        {
            return new ManagedFileOperationResult(
                classification,
                false,
                false,
                "No recorded TypeWhisper publication exists."
            );
        }

        if (
            classification is not (
                ManagedFileClassification.CurrentOwned
                or ManagedFileClassification.StaleOwned
            )
        )
        {
            return Refused(classification);
        }

        var preimage = await ReadValidPreimageAsync(spec, paths, state, ct).ConfigureAwait(false);
        var restore = spec.RemovalPolicy == ManagedFileRemovalPolicy.RestorePreimageIfUnchanged
            && preimage is not null;
        var stage = string.Empty;
        if (restore)
        {
            stage = await StageAsync(
                    spec.DestinationPath,
                    preimage!.Value.Bytes,
                    preimage.Value.Mode,
                    ct
                )
                .ConfigureAwait(false);
        }

        try
        {
            var journal = new ManagedFileJournal
            {
                Operation = ManagedFileJournalOperation.Remove,
                ArtifactId = spec.ArtifactId,
                DestinationPath = spec.DestinationPath,
                OldExists = true,
                OldMode = (int)captured.Mode,
                NewExists = restore,
                NewMode = restore ? (int)preimage!.Value.Mode : null,
            };
            await WriteJournalAsync(
                    paths,
                    journal,
                    captured.Bytes,
                    restore ? preimage!.Value.Bytes : null,
                    journalPreimage: null,
                    ct
                )
                .ConfigureAwait(false);
            await CheckpointAsync(ManagedFileCheckpoint.RemoveAfterJournal, ct)
                .ConfigureAwait(false);

            var current = await CaptureEntryAsync(spec.DestinationPath, ct).ConfigureAwait(false);
            if (!EntryEquals(captured, current))
            {
                throw new ManagedFileConcurrencyException(
                    $"'{spec.DestinationPath}' changed while '{spec.ArtifactId}' was being removed."
                );
            }

            ct.ThrowIfCancellationRequested();
            if (restore)
            {
                File.Move(stage, spec.DestinationPath, true);
                stage = string.Empty;
            }
            else
            {
                File.Delete(spec.DestinationPath);
            }

            await CheckpointAsync(ManagedFileCheckpoint.RemoveAfterPublish, ct)
                .ConfigureAwait(false);
            DeleteState(paths);
            await CheckpointAsync(ManagedFileCheckpoint.RemoveAfterState, ct)
                .ConfigureAwait(false);
            DeleteJournal(paths);
        }
        finally
        {
            DeleteBestEffort(stage);
        }

        if (spec.PostRemove is not null)
        {
            await spec.PostRemove(ct).ConfigureAwait(false);
        }

        return new ManagedFileOperationResult(classification, true, false);
    }

    private static async Task RecoverAsync(
        ManagedFileSpec spec,
        ArtifactPaths paths,
        CancellationToken ct
    )
    {
        if (!File.Exists(paths.Journal))
        {
            return;
        }

        var journal = await ReadJsonAsync<ManagedFileJournal>(paths.Journal, ct)
            .ConfigureAwait(false);
        if (
            journal.Version != 1
            || !string.Equals(journal.ArtifactId, spec.ArtifactId, StringComparison.Ordinal)
            || !PathEquals(journal.DestinationPath, spec.DestinationPath)
        )
        {
            throw new InvalidDataException(
                $"Pending journal for '{spec.ArtifactId}' does not match its specification."
            );
        }

        var current = await CaptureEntryAsync(spec.DestinationPath, ct).ConfigureAwait(false);
        var matchesOld = await MatchesJournalImageAsync(
                current,
                journal.OldExists,
                journal.OldMode,
                paths.JournalOld,
                ct
            )
            .ConfigureAwait(false);
        var matchesNew = await MatchesJournalImageAsync(
                current,
                journal.NewExists,
                journal.NewMode,
                paths.JournalNew,
                ct
            )
            .ConfigureAwait(false);

        if (matchesNew)
        {
            if (journal.Operation == ManagedFileJournalOperation.Install)
            {
                await FinalizeInstallAsync(spec, paths, journal, ct).ConfigureAwait(false);
            }
            else
            {
                DeleteState(paths);
            }

            DeleteJournal(paths);
            return;
        }

        if (!matchesOld)
        {
            throw new ManagedFileRecoveryConflictException(
                $"Pending operation for '{spec.ArtifactId}' cannot be recovered because "
                + $"'{spec.DestinationPath}' matches neither its exact old nor new image."
            );
        }

        DeleteJournal(paths);
    }

    private static async Task<ManagedFileClassification> ClassifyAsync(
        ManagedFileSpec spec,
        ArtifactPaths paths,
        ManagedFileState? state,
        EntrySnapshot entry,
        CancellationToken ct
    )
    {
        if (entry.Kind == EntryKind.Absent)
        {
            return ManagedFileClassification.Absent;
        }

        if (entry.Kind != EntryKind.Regular || entry.Bytes is null)
        {
            return ManagedFileClassification.UnsupportedEntry;
        }

        if (state is not null)
        {
            var published = await ReadRequiredImageAsync(paths.Published, ct).ConfigureAwait(false);
            if (!string.Equals(Sha256(published), state.PublishedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Recorded publication for '{spec.ArtifactId}' is corrupt."
                );
            }

            if (!entry.Bytes.AsSpan().SequenceEqual(published))
            {
                return ManagedFileClassification.CustomizedOwned;
            }

            var desired = spec.DesiredBytes;
            // ReSharper disable once InvertIf -- inverting would duplicate the two-line comparison
            // return below just to flatten this one optional override.
            if (
                spec is
                {
                    ExistingPolicy: ManagedFileExistingPolicy.BackupTransformAndRestore,
                    BackupTransform: not null,
                }
            )
            {
                var preimage = await ReadValidPreimageAsync(spec, paths, state, ct)
                    .ConfigureAwait(false);
                if (preimage is not null)
                {
                    desired = spec.BackupTransform(preimage.Value.Bytes);
                }
            }

            return entry.Bytes.AsSpan().SequenceEqual(desired)
                ? CurrentOrStaleByMode(spec, entry)
                : ManagedFileClassification.StaleOwned;
        }

        if (spec.LegacyPreimagePath is not null && spec.BackupTransform is not null)
        {
            var legacyPreimage = await CaptureEntryAsync(spec.LegacyPreimagePath, ct)
                .ConfigureAwait(false);
            if (legacyPreimage.Kind != EntryKind.Regular || legacyPreimage.Bytes is null)
            {
                return ManagedFileClassification.UnsupportedEntry;
            }

            if (
                spec.BackupTransform(legacyPreimage.Bytes)
                    .AsSpan()
                    .SequenceEqual(entry.Bytes)
            )
            {
                return ManagedFileClassification.CurrentOwned;
            }
        }

        if (entry.Bytes.AsSpan().SequenceEqual(spec.DesiredBytes))
        {
            return spec.OwnershipProbe(entry.Bytes)
                ? CurrentOrStaleByMode(spec, entry)
                : ManagedFileClassification.EquivalentForeign;
        }

        if (spec.LegacyOwnershipProbe?.Invoke(entry.Bytes) == true)
        {
            return ManagedFileClassification.StaleOwned;
        }

        if (spec.OwnershipProbe(entry.Bytes))
        {
            return ManagedFileClassification.CustomizedOwned;
        }

        return spec.EquivalentContentProbe?.Invoke(entry.Bytes) == true
            ? ManagedFileClassification.EquivalentForeign
            : ManagedFileClassification.Foreign;
    }

    /// <summary>
    ///     Mode is part of a managed whole file's published image, so bytes alone do not
    ///     make it current: a chmod that diverges from <see cref="ManagedFileSpec.CreateMode" />
    ///     is drift we republish (stale), not a content customization we refuse — otherwise
    ///     `chmod -x` on the installed CLI would report as installed forever and never be
    ///     repaired. Backup/transform artifacts are exempt: their mode is restored from the
    ///     user's own preimage, so it is deliberately not ours to normalize.
    /// </summary>
    private static ManagedFileClassification CurrentOrStaleByMode(
        ManagedFileSpec spec,
        EntrySnapshot entry
    )
    {
        return OperatingSystem.IsWindows()
            || spec.ExistingPolicy == ManagedFileExistingPolicy.BackupTransformAndRestore
            || entry.Mode == spec.CreateMode
            ? ManagedFileClassification.CurrentOwned
            : ManagedFileClassification.StaleOwned;
    }

    private static ManagedFileOperationResult Refused(ManagedFileClassification classification)
    {
        return new ManagedFileOperationResult(
            classification,
            false,
            false,
            "Destination is foreign, customized, symlinked, or otherwise unsupported."
        );
    }

    private static async Task AdoptCurrentAsync(
        ManagedFileSpec spec,
        ArtifactPaths paths,
        EntrySnapshot captured,
        CancellationToken ct
    )
    {
        if (captured.Bytes is null)
        {
            throw new InvalidOperationException("Cannot adopt a non-file destination.");
        }

        byte[]? preimageBytes = null;
        UnixFileMode? preimageMode = null;
        if (spec.LegacyPreimagePath is not null)
        {
            var preimage = await CaptureEntryAsync(spec.LegacyPreimagePath, ct)
                .ConfigureAwait(false);
            if (preimage.Kind != EntryKind.Regular || preimage.Bytes is null)
            {
                throw new InvalidDataException(
                    $"Legacy preimage for '{spec.ArtifactId}' is missing or unsafe."
                );
            }

            if (
                spec.BackupTransform is null
                || !spec.BackupTransform(preimage.Bytes).AsSpan().SequenceEqual(captured.Bytes)
            )
            {
                throw new InvalidDataException(
                    $"Legacy preimage for '{spec.ArtifactId}' does not reproduce its publication."
                );
            }

            preimageBytes = preimage.Bytes;
            preimageMode = preimage.Mode;
            await WritePrivateFileAsync(paths.Preimage, preimageBytes, ct).ConfigureAwait(false);
        }

        await WritePrivateFileAsync(paths.Published, captured.Bytes, ct).ConfigureAwait(false);
        var state = new ManagedFileState
        {
            ArtifactId = spec.ArtifactId,
            DestinationPath = spec.DestinationPath,
            PublishedSha256 = Sha256(captured.Bytes),
            PublishedMode = (int)captured.Mode,
            PreimageSha256 = preimageBytes is null ? null : Sha256(preimageBytes),
            PreimageMode = preimageMode is null ? null : (int)preimageMode.Value,
        };
        await WriteJsonAtomicAsync(paths.State, state, ct).ConfigureAwait(false);
    }

    private static async Task FinalizeInstallAsync(
        ManagedFileSpec spec,
        ArtifactPaths paths,
        ManagedFileJournal journal,
        CancellationToken ct
    )
    {
        var published = await ReadRequiredImageAsync(paths.JournalNew, ct).ConfigureAwait(false);
        await WritePrivateFileAsync(paths.Published, published, ct).ConfigureAwait(false);

        if (journal.FinalPreimageSha256 is not null)
        {
            byte[] preimage;
            if (journal.JournalCarriesPreimage)
            {
                preimage = await ReadRequiredImageAsync(paths.JournalPreimage, ct)
                    .ConfigureAwait(false);
                await WritePrivateFileAsync(paths.Preimage, preimage, ct).ConfigureAwait(false);
            }
            else
            {
                preimage = await ReadRequiredImageAsync(paths.Preimage, ct).ConfigureAwait(false);
            }

            if (
                !string.Equals(
                    Sha256(preimage),
                    journal.FinalPreimageSha256,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    $"Preimage for '{spec.ArtifactId}' does not match the pending journal."
                );
            }
        }
        else
        {
            DeleteBestEffort(paths.Preimage);
        }

        var state = new ManagedFileState
        {
            ArtifactId = spec.ArtifactId,
            DestinationPath = spec.DestinationPath,
            PublishedSha256 = Sha256(published),
            PublishedMode = journal.NewMode
                ?? throw new InvalidDataException("Install journal has no publication mode."),
            PreimageSha256 = journal.FinalPreimageSha256,
            PreimageMode = journal.FinalPreimageMode,
        };
        await WriteJsonAtomicAsync(paths.State, state, ct).ConfigureAwait(false);
    }

    private static async Task<(byte[] Bytes, UnixFileMode Mode)?> ReadValidPreimageAsync(
        ManagedFileSpec spec,
        ArtifactPaths paths,
        ManagedFileState? state,
        CancellationToken ct
    )
    {
        if (state?.PreimageSha256 is null)
        {
            return null;
        }

        if (state.PreimageMode is null)
        {
            throw new InvalidDataException($"Preimage mode for '{spec.ArtifactId}' is missing.");
        }

        var bytes = await ReadRequiredImageAsync(paths.Preimage, ct).ConfigureAwait(false);
        return string.Equals(Sha256(bytes), state.PreimageSha256, StringComparison.Ordinal)
            ? (bytes, (UnixFileMode)state.PreimageMode.Value)
            : throw new InvalidDataException($"Preimage for '{spec.ArtifactId}' is corrupt.");
    }

    private static async Task<ManagedFileState?> TryReadStateAsync(
        ManagedFileSpec spec,
        ArtifactPaths paths,
        CancellationToken ct
    )
    {
        if (!File.Exists(paths.State))
        {
            if (File.Exists(paths.Published) || File.Exists(paths.Preimage))
            {
                throw new InvalidDataException(
                    $"Managed state for '{spec.ArtifactId}' is incomplete."
                );
            }

            return null;
        }

        var state = await ReadJsonAsync<ManagedFileState>(paths.State, ct).ConfigureAwait(false);
        if (
            state.Version != 1
            || !string.Equals(state.ArtifactId, spec.ArtifactId, StringComparison.Ordinal)
            || !PathEquals(state.DestinationPath, spec.DestinationPath)
        )
        {
            throw new InvalidDataException(
                $"Managed state for '{spec.ArtifactId}' does not match its specification."
            );
        }

        return state;
    }

    private static async Task WriteJournalAsync(
        ArtifactPaths paths,
        ManagedFileJournal journal,
        byte[]? oldBytes,
        byte[]? newBytes,
        byte[]? journalPreimage,
        CancellationToken ct
    )
    {
        if (journal.OldExists)
        {
            await WritePrivateFileAsync(
                    paths.JournalOld,
                    oldBytes ?? throw new InvalidOperationException("Old journal image is missing."),
                    ct
                )
                .ConfigureAwait(false);
        }
        else
        {
            DeleteBestEffort(paths.JournalOld);
        }

        if (journal.NewExists)
        {
            await WritePrivateFileAsync(
                    paths.JournalNew,
                    newBytes ?? throw new InvalidOperationException("New journal image is missing."),
                    ct
                )
                .ConfigureAwait(false);
        }
        else
        {
            DeleteBestEffort(paths.JournalNew);
        }

        if (journal.JournalCarriesPreimage)
        {
            await WritePrivateFileAsync(
                    paths.JournalPreimage,
                    journalPreimage
                    ?? throw new InvalidOperationException("Journal preimage is missing."),
                    ct
                )
                .ConfigureAwait(false);
        }
        else
        {
            DeleteBestEffort(paths.JournalPreimage);
        }

        await WriteJsonAtomicAsync(paths.Journal, journal, ct).ConfigureAwait(false);
    }

    private static async Task<bool> MatchesJournalImageAsync(
        EntrySnapshot entry,
        bool expectedExists,
        int? expectedMode,
        string imagePath,
        CancellationToken ct
    )
    {
        if (!expectedExists)
        {
            return entry.Kind == EntryKind.Absent;
        }

        if (entry.Kind != EntryKind.Regular || entry.Bytes is null || expectedMode is null)
        {
            return false;
        }

        var expected = await ReadRequiredImageAsync(imagePath, ct).ConfigureAwait(false);
        return entry.Bytes.AsSpan().SequenceEqual(expected)
            && entry.Mode == (UnixFileMode)expectedMode.Value;
    }

    private static async Task<EntrySnapshot> CaptureEntryAsync(string path, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(path);
        var requestedName = Path.GetFileName(path);
        if (
            !string.IsNullOrEmpty(parent)
            && !string.IsNullOrEmpty(requestedName)
            && NativeFile.GetEntryKind(parent) == EntryKind.Directory
        )
        {
            var aliases = Directory
                .EnumerateFileSystemEntries(parent, "*", new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                })
                .Select(Path.GetFileName)
                .Where(name =>
                    string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase)
                )
                .ToArray();
            if (
                aliases.Length > 0
                && !aliases.Any(name =>
                    string.Equals(name, requestedName, StringComparison.Ordinal)
                )
            )
            {
                return new EntrySnapshot(EntryKind.Unsupported, null, default);
            }
        }

        var kind = NativeFile.GetEntryKind(path);
        if (kind != EntryKind.Regular)
        {
            return new EntrySnapshot(kind, null, default);
        }

        return await NativeFile.ReadRegularFileAsync(path, ct).ConfigureAwait(false);
    }

    private static bool EntryEquals(EntrySnapshot left, EntrySnapshot right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        if (left.Kind != EntryKind.Regular)
        {
            return true;
        }

        return left.Mode == right.Mode
            && left.Bytes is not null
            && right.Bytes is not null
            && left.Bytes.AsSpan().SequenceEqual(right.Bytes);
    }

    private static async Task<string> StageAsync(
        string destination,
        byte[] bytes,
        UnixFileMode mode,
        CancellationToken ct
    )
    {
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Destination must have a parent directory.", nameof(destination));
        }

        Directory.CreateDirectory(directory);
        var stage = Path.Join(
            directory,
            $".{Path.GetFileName(destination)}.{Path.GetRandomFileName()}.tmp"
        );
        try
        {
            await WriteNewFileAsync(stage, bytes, mode, ct).ConfigureAwait(false);
            VerifyMode(stage, mode);
            return stage;
        }
        catch
        {
            DeleteBestEffort(stage);
            throw;
        }
    }

    private static async Task WritePrivateFileAsync(
        string path,
        byte[] bytes,
        CancellationToken ct
    )
    {
        var stage = Path.Join(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Path.GetRandomFileName()}.tmp"
        );
        try
        {
            await WriteNewFileAsync(stage, bytes, PrivateFileMode, ct).ConfigureAwait(false);
            VerifyMode(stage, PrivateFileMode);
            File.Move(stage, path, true);
            stage = string.Empty;
        }
        finally
        {
            DeleteBestEffort(stage);
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        await WritePrivateFileAsync(
                path,
                JsonSerializer.SerializeToUtf8Bytes(value, s_jsonOptions),
                ct
            )
            .ConfigureAwait(false);
    }

    private static async Task WriteNewFileAsync(
        string path,
        byte[] bytes,
        UnixFileMode mode,
        CancellationToken ct
    )
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = mode;
        }

        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static void VerifyMode(string path, UnixFileMode expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var actual = File.GetUnixFileMode(path);
        if (actual != expected)
        {
            throw new IOException(
                $"Could not apply mode {expected} to '{path}'; found {actual}."
            );
        }
    }

    private ArtifactPaths PrepareStatePaths(string artifactId)
    {
        EnsurePrivateDirectory(_stateRoot);
        var artifactDirectory = Path.Join(_stateRoot, artifactId);
        EnsurePrivateDirectory(artifactDirectory);
        return new ArtifactPaths(artifactDirectory);
    }

    private static void EnsurePrivateDirectory(string path)
    {
        var kind = NativeFile.GetEntryKind(path);
        if (kind is not (EntryKind.Absent or EntryKind.Directory))
        {
            throw new IOException($"Managed-artifact state path '{path}' is not a directory.");
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, PrivateDirectoryMode);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
        VerifyMode(path, PrivateDirectoryMode);
    }

    private static async Task<ArtifactLock> AcquireLockAsync(
        ArtifactPaths paths,
        CancellationToken ct
    )
    {
        var processLock = s_processLocks.GetOrAdd(paths.Lock, _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(ct).ConfigureAwait(false);
        FileStream? stream = null;
        try
        {
            var lockKind = NativeFile.GetEntryKind(paths.Lock);
            if (lockKind is not (EntryKind.Absent or EntryKind.Regular))
            {
                throw new IOException(
                    $"Managed-artifact lock '{paths.Lock}' is not a regular file."
                );
            }

            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            stream = new FileStream(paths.Lock, options);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(paths.Lock, PrivateFileMode);
                VerifyMode(paths.Lock, PrivateFileMode);
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        throw new PlatformNotSupportedException(
                            "Managed artifact locking requires Linux or Windows file locks."
                        );
                    }

                    stream.Lock(0, 1);
                    return new ArtifactLock(stream, processLock);
                }
                catch (IOException)
                {
                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            processLock.Release();
            throw;
        }
    }

    private Task CheckpointAsync(ManagedFileCheckpoint checkpoint, CancellationToken ct)
    {
        return _checkpoint?.Invoke(checkpoint, ct) ?? Task.CompletedTask;
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        var bytes = await ReadRequiredImageAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, s_jsonOptions)
            ?? throw new InvalidDataException($"'{path}' contains no valid state.");
    }

    private static async Task<byte[]> ReadRequiredImageAsync(string path, CancellationToken ct)
    {
        var entry = await CaptureEntryAsync(path, ct).ConfigureAwait(false);
        if (entry.Kind != EntryKind.Regular || entry.Bytes is null)
        {
            throw new InvalidDataException($"Required state image '{path}' is missing or unsafe.");
        }

        if (!OperatingSystem.IsWindows() && entry.Mode != PrivateFileMode)
        {
            throw new InvalidDataException($"State image '{path}' is not mode 0600.");
        }

        return entry.Bytes;
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void DeleteState(ArtifactPaths paths)
    {
        DeleteBestEffort(paths.State);
        DeleteBestEffort(paths.Published);
        DeleteBestEffort(paths.Preimage);
    }

    private static void DeleteJournal(ArtifactPaths paths)
    {
        DeleteBestEffort(paths.Journal);
        DeleteBestEffort(paths.JournalOld);
        DeleteBestEffort(paths.JournalNew);
        DeleteBestEffort(paths.JournalPreimage);
    }

    private static void DeleteBestEffort(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[ManagedFileTransaction] could not clean '{path}': {ex.Message}");
        }
    }

    private static bool PathEquals(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static void ValidateSpec(ManagedFileSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (
            string.IsNullOrWhiteSpace(spec.ArtifactId)
            || spec.ArtifactId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            )
            || spec.ArtifactId is "." or ".."
        )
        {
            throw new ArgumentException("ArtifactId contains unsafe path characters.", nameof(spec));
        }

        if (!Path.IsPathFullyQualified(spec.DestinationPath))
        {
            throw new ArgumentException("DestinationPath must be absolute.", nameof(spec));
        }

        ArgumentNullException.ThrowIfNull(spec.DesiredBytes);
        ArgumentNullException.ThrowIfNull(spec.OwnershipProbe);
        // Validated for every operation, not just removal: classification reads this path too,
        // and a relative one would resolve against the process working directory.
        if (
            spec.LegacyPreimagePath is not null
            && (!Path.IsPathFullyQualified(spec.LegacyPreimagePath)
                || spec.ExistingPolicy != ManagedFileExistingPolicy.BackupTransformAndRestore)
        )
        {
            throw new ArgumentException(
                "LegacyPreimagePath must be absolute and belongs only to backup/transform artifacts.",
                nameof(spec)
            );
        }

        if (
            spec.ExistingPolicy == ManagedFileExistingPolicy.BackupTransformAndRestore
            && (spec.BackupTransform is null
                || spec.RemovalPolicy != ManagedFileRemovalPolicy.RestorePreimageIfUnchanged)
        )
        {
            throw new ArgumentException(
                "Backup/transform artifacts require a transform and restore removal policy.",
                nameof(spec)
            );
        }
    }

    private readonly record struct ArtifactPaths(string Directory)
    {
        public string State => Path.Join(Directory, StateFileName);
        public string Published => Path.Join(Directory, PublishedImageName);
        public string Preimage => Path.Join(Directory, PreimageName);
        public string Journal => Path.Join(Directory, JournalFileName);
        public string JournalOld => Path.Join(Directory, JournalOldImageName);
        public string JournalNew => Path.Join(Directory, JournalNewImageName);
        public string JournalPreimage => Path.Join(Directory, JournalPreimageName);
        public string Lock => Path.Join(Directory, LockFileName);
    }

    private sealed class ArtifactLock(FileStream stream, SemaphoreSlim processLock)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // A throwing close must not strand the semaphore: every later operation on this
            // artifact would wait on a permit nobody can release.
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                processLock.Release();
            }
        }
    }

    private readonly record struct EntrySnapshot(
        EntryKind Kind,
        byte[]? Bytes,
        UnixFileMode Mode
    );

    private enum EntryKind
    {
        Absent,
        Regular,
        Directory,
        Symlink,
        Unsupported,
    }

    private static partial class NativeFile
    {
        private const int AtFdcwd = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const int AtEmptyPath = 0x1000;
        private const uint StatxType = 0x0001;

        // libc O_* open(2) flags and S_IF* st_mode file-type bits, named to match the
        // PascalCase the rest of this block already uses for AT_* / STATX_*.
        private const int OpenReadOnly = 0;
        private const int OpenCloseOnExec = 0x80000;
        private const int OpenNoFollow = 0x20000;
        private const int OpenNonBlock = 0x800;
        private const ushort FileTypeMask = 0xF000;
        private const ushort FileTypeRegular = 0x8000;
        private const ushort FileTypeDirectory = 0x4000;
        private const ushort FileTypeSymlink = 0xA000;

        public static EntryKind GetEntryKind(string path)
        {
            if (!OperatingSystem.IsLinux())
            {
                return GetPortableEntryKind(path);
            }

            var result = statx(AtFdcwd, path, AtSymlinkNoFollow, StatxType, out var stat);
            if (result == 0)
            {
                return KindFromMode(stat.Mode);
            }

            var error = Marshal.GetLastPInvokeError();
            return error is 2 or 20
                ? EntryKind.Absent
                : throw new Win32Exception(error, $"Could not inspect '{path}'.");
        }

        public static async Task<EntrySnapshot> ReadRegularFileAsync(
            string path,
            CancellationToken ct
        )
        {
            if (!OperatingSystem.IsLinux())
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var mode = OperatingSystem.IsWindows()
                    ? default
                    : File.GetUnixFileMode(path);
                return new EntrySnapshot(EntryKind.Regular, bytes, mode);
            }

            var fd = open(path, OpenReadOnly | OpenCloseOnExec | OpenNoFollow | OpenNonBlock, 0);
            if (fd < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 2 or 20 or 40)
                {
                    throw new ManagedFileConcurrencyException(
                        $"'{path}' changed while it was being inspected."
                    );
                }

                throw new Win32Exception(error, $"Could not open '{path}' safely.");
            }

            var handle = new SafeFileHandle(fd, ownsHandle: true);
            try
            {
                if (statx(fd, string.Empty, AtEmptyPath, StatxType, out var stat) != 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        $"Could not inspect open file '{path}'."
                    );
                }

                if (KindFromMode(stat.Mode) != EntryKind.Regular)
                {
                    throw new ManagedFileConcurrencyException(
                        $"'{path}' stopped being a regular file while it was inspected."
                    );
                }

                await using var stream = new FileStream(handle, FileAccess.Read);
                handle = null!;
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                return new EntrySnapshot(
                    EntryKind.Regular,
                    buffer.ToArray(),
                    (UnixFileMode)(stat.Mode & 0x0FFF)
                );
            }
            finally
            {
                // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- handle is deliberately set to null! once FileStream takes ownership; the ?. is what stops a double dispose.
                handle?.Dispose();
            }
        }

        private static EntryKind GetPortableEntryKind(string path)
        {
            try
            {
                var info = new FileInfo(path);
                info.Refresh();
                if (info.LinkTarget is not null)
                {
                    return EntryKind.Symlink;
                }

                if (!info.Exists)
                {
                    return Directory.Exists(path) ? EntryKind.Directory : EntryKind.Absent;
                }

                return (info.Attributes & FileAttributes.Directory) != 0
                    ? EntryKind.Directory
                    : EntryKind.Regular;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return EntryKind.Absent;
            }
        }

        private static EntryKind KindFromMode(ushort mode)
        {
            return (ushort)(mode & FileTypeMask) switch
            {
                FileTypeRegular => EntryKind.Regular,
                FileTypeDirectory => EntryKind.Directory,
                FileTypeSymlink => EntryKind.Symlink,
                _ => EntryKind.Unsupported,
            };
        }

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct StatxBuffer
        {
            public uint Mask;
            public uint BlockSize;
            public ulong Attributes;
            public uint LinkCount;
            public uint UserId;
            public uint GroupId;
            public ushort Mode;
        }

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int statx(
            int directoryFileDescriptor,
            string path,
            int flags,
            uint mask,
            out StatxBuffer buffer
        );

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int open(string path, int flags, uint mode);
    }
}

public sealed class ManagedFileConcurrencyException(string message) : IOException(message);

public sealed class ManagedFileRecoveryConflictException(string message) : IOException(message);
