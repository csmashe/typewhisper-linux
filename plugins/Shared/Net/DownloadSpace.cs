using System.Globalization;

// File-linked like ResilientDownloader. Each linked copy uses a different subset
// (Gemma has no ResilientDownloader; only tests read the byte counts), so
// per-assembly usage checks misfire.
// ReSharper disable CheckNamespace, MemberCanBePrivate.Global, UnusedType.Global
// ReSharper disable NotAccessedPositionalProperty.Global, UnusedAutoPropertyAccessor.Global
namespace TypeWhisper.Plugins.Shared.Net;

/// <summary>
///     Free-space policy for on-demand model and runtime downloads: a download and
///     its extraction are checked against the target filesystem before writing, and
///     a write that still runs out of space is reported the same way.
///     <para>
///         Nothing is deleted to make room; callers only clean up their own staging files.
///     </para>
/// </summary>
internal static class DownloadSpace
{
    /// <summary>Headroom kept free beyond the bytes a download needs.</summary>
    internal const long ReserveBytes = 256L * 1024 * 1024;

    // Linux errno values; .NET reports them as the IOException HResult on Unix.
    private const int Enospc = 28;
    private const int Edquot = 122;

    /// <summary>
    ///     Bytes available to this user on the filesystem holding <paramref name="path" />,
    ///     or null when unknown. A missing path resolves to its nearest existing ancestor;
    ///     symlinks are followed to their real mount.
    /// </summary>
    public static long? GetAvailableBytes(string path)
    {
        try
        {
            var existing = NearestExistingDirectory(path);
            if (existing is null)
                return null;
            var drive = new DriveInfo(existing);
            return FromCapacity(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // Pseudo filesystems (proc, sysfs) report no capacity at all and are unknown;
    // a real filesystem with zero available bytes is genuinely full.
    internal static long? FromCapacity(long totalBytes, long availableBytes) =>
        totalBytes > 0 ? Math.Max(0, availableBytes) : null;

    /// <summary>
    ///     Throws <see cref="InsufficientDownloadSpaceException" /> when the filesystem
    ///     holding <paramref name="path" /> has less than <paramref name="requiredBytes" />
    ///     plus <see cref="ReserveBytes" /> free. An unknown probe passes; the write-time
    ///     mapping still reports a real shortage. <paramref name="probe" /> is a test seam.
    /// </summary>
    public static void EnsureAvailable(
        string path,
        long requiredBytes,
        string description,
        Func<string, long?>? probe = null
    )
    {
        if (requiredBytes <= 0)
            return;
        var available = (probe ?? GetAvailableBytes)(path);
        if (available is null || available.Value >= requiredBytes + ReserveBytes)
            return;
        throw new InsufficientDownloadSpaceException(
            description,
            LocationOf(path),
            requiredBytes + ReserveBytes,
            available.Value
        );
    }

    /// <summary>True when <paramref name="exception" /> (or an inner exception) is a full disk or exceeded quota.</summary>
    public static bool IsOutOfSpace(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 16; depth++)
        {
            // InsufficientDownloadSpaceException carries ENOSPC, so the errno check covers it.
            if (
                exception is IOException { HResult: Enospc or Edquot }
                || (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsOutOfSpace))
            )
                return true;
            exception = exception.InnerException;
        }
        return false;
    }

    /// <summary>
    ///     Converts a write failure caused by a full disk or quota into an
    ///     <see cref="InsufficientDownloadSpaceException" />; returns null for any
    ///     other failure so the caller rethrows the original.
    /// </summary>
    public static InsufficientDownloadSpaceException? TranslateWriteFailure(
        Exception exception,
        string path,
        string description
    ) =>
        exception switch
        {
            InsufficientDownloadSpaceException existing => existing,
            _ when IsOutOfSpace(exception) => new InsufficientDownloadSpaceException(
                description,
                LocationOf(path),
                exception
            ),
            _ => null,
        };

    internal static string FormatBytes(long bytes, bool roundUp)
    {
        const double mib = 1024d * 1024;
        const double gib = mib * 1024;
        var (value, unit) = bytes >= gib ? (bytes / gib, "GB") : (bytes / mib, "MB");
        value = roundUp ? Math.Ceiling(value * 10) / 10 : Math.Floor(value * 10) / 10;
        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit;
    }

    private static string? NearestExistingDirectory(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
                return null;
            current = parent;
        }
        return current;
    }

    // Name the directory the user can act on, not a staging file inside it.
    private static string LocationOf(string path)
    {
        var full = Path.GetFullPath(path);
        return Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
    }
}

/// <summary>
///     A download or extraction cannot fit on its target filesystem. Carries ENOSPC
///     so callers classifying storage failures by errno keep working.
/// </summary>
internal sealed class InsufficientDownloadSpaceException : IOException
{
    private const int Enospc = 28;

    public InsufficientDownloadSpaceException(
        string description,
        string location,
        long requiredBytes,
        long availableBytes
    )
        : base(
            $"Not enough disk space for {description}: {DownloadSpace.FormatBytes(requiredBytes, roundUp: true)} "
                + $"must be free in {location} (including a {DownloadSpace.FormatBytes(DownloadSpace.ReserveBytes, roundUp: true)} "
                + $"safety margin), but only {DownloadSpace.FormatBytes(availableBytes, roundUp: false)} is available. "
                + $"Free up at least {DownloadSpace.FormatBytes(requiredBytes - availableBytes, roundUp: true)} and try again."
        )
    {
        HResult = Enospc;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    public InsufficientDownloadSpaceException(string description, string location, Exception inner)
        : base(
            $"Ran out of disk space while writing {description} to {location}. Free up space and try again.",
            inner
        )
    {
        HResult = Enospc;
    }

    public long? RequiredBytes { get; }
    public long? AvailableBytes { get; }
}

/// <summary>
///     What a <c>ResilientDownloader</c> download must fit: the remaining body plus
///     <paramref name="AdditionalBytes" /> written afterwards (e.g. extracted files).
///     <paramref name="Probe" /> is a test seam.
/// </summary>
internal sealed record DownloadSpaceRequirement(
    string Description,
    long AdditionalBytes = 0,
    Func<string, long?>? Probe = null
);
