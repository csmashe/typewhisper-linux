using System.Text;

namespace TypeWhisper.Linux.Services.ManagedArtifacts;

public enum ManagedFileExistingPolicy
{
    RefuseForeign,
    AcceptEquivalentWithoutOwning,
    BackupTransformAndRestore,
}

public enum ManagedFileRemovalPolicy
{
    DeleteIfUnchanged,
    RestorePreimageIfUnchanged,
}

/// <summary>
///     Describes one installer-owned, whole-file destination. Container files with
///     user-owned content belong in a fragment editor and must not use this type.
/// </summary>
public sealed record ManagedFileSpec
{
    public required string ArtifactId { get; init; }
    public required string DestinationPath { get; init; }
    public required byte[] DesiredBytes { get; init; }
    public required UnixFileMode CreateMode { get; init; }
    public required Func<ReadOnlyMemory<byte>, bool> OwnershipProbe { get; init; }
    public Func<ReadOnlyMemory<byte>, bool>? LegacyOwnershipProbe { get; init; }
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  read by ManagedFileTransaction.ClassifyAsync; no in-tree spec sets it yet
    public Func<ReadOnlyMemory<byte>, bool>? EquivalentContentProbe { get; init; }
    public Func<ReadOnlyMemory<byte>, byte[]>? BackupTransform { get; init; }
    public string? LegacyPreimagePath { get; init; }
    public ManagedFileExistingPolicy ExistingPolicy { get; init; } =
        ManagedFileExistingPolicy.RefuseForeign;
    public ManagedFileRemovalPolicy RemovalPolicy { get; init; } =
        ManagedFileRemovalPolicy.DeleteIfUnchanged;
    public Func<string, CancellationToken, Task>? StagedFileValidator { get; init; }
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  awaited by InstallAsync; no in-tree spec sets it yet
    public Func<CancellationToken, Task>? PostCommit { get; init; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global  awaited by RemoveAsync; no in-tree spec sets it yet
    public Func<CancellationToken, Task>? PostRemove { get; init; }

    public static byte[] Utf8(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return Encoding.UTF8.GetBytes(contents);
    }
}
