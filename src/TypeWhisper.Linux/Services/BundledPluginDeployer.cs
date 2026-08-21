using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services.ManagedArtifacts;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Copies bundled plugins from the app's install directory into
///     <c>~/.local/share/TypeWhisper/Plugins/</c>, repairing stale or partially
///     deployed bundled plugins on every startup while leaving non-bundled /
///     manually installed plugins alone.
/// </summary>
public sealed class BundledPluginDeployer
{
    private const string StampFileName = ".typewhisper-bundle.sha256";
    private const string BundleIdentityFileName = ".typewhisper-bundle-identity.sha256";
    private const string ScratchDirectoryName = ".typewhisper-deploy";

    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the count of synced plugins for callers that want it; the current caller ignores it.
    public static int DeployIfMissing()
    {
        var source = FindBundledPluginsDir();
        // ReSharper disable once InvertIf -- guard clause; inverting would bury the skip trace.
        if (source is null)
        {
            Trace.WriteLine(
                "[BundledPluginDeployer] No bundled Plugins/ dir next to executable — skipping."
            );
            return 0;
        }

        return DeployIfMissing(source, TypeWhisperEnvironment.PluginsPath);
    }

    internal static int DeployIfMissing(
        string sourceRoot,
        string destRoot,
        Action<string, string>? copyFile = null,
        Action<string>? afterCommit = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destRoot);

        copyFile ??= static (source, destination) => File.Copy(source, destination);
        Directory.CreateDirectory(destRoot);

        // Packaging writes this identity only after the bundled payload is final. A
        // valid marker therefore attests to the sorted paths and contents of the whole
        // source tree; runtime trusts it instead of re-hashing published source files.
        // The marker is an OPAQUE token: the packaging script's hash algorithm differs
        // from ComputeFingerprints, and the only valid comparison is source marker vs
        // installed marker — never marker vs a locally computed hash.
        var hasPublishedIdentity = TryReadBundleIdentity(sourceRoot, out var sourceIdentity);
        if (!hasPublishedIdentity)
        {
            // An unmarked (dev/legacy) run may deploy a different plugin tree while the
            // installed marker still vouches for the last marked build. Left in place, a
            // later launch of that same marked build would trust the marker and accept
            // the unmarked tree as current, so retract the attestation up front.
            InvalidateInstalledIdentity(destRoot);
        }

        var installedIdentityCurrent =
            hasPublishedIdentity
            && TryReadBundleIdentity(destRoot, out var installedIdentity)
            && DigestsEqual(sourceIdentity, installedIdentity);
        var forceDeploy = hasPublishedIdentity && !installedIdentityCurrent;
        var allPluginsSucceeded = true;
        var deployed = 0;
        foreach (
            var pluginDir in Directory
                .GetDirectories(sourceRoot)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
        )
        {
            var name = Path.GetFileName(pluginDir);
            var dest = Path.Join(destRoot, name);

            try
            {
                if (string.Equals(name, ScratchDirectoryName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Bundled plugin name is reserved: {ScratchDirectoryName}"
                    );
                }

                if (
                    DeployPlugin(
                        pluginDir,
                        destRoot,
                        dest,
                        name,
                        copyFile,
                        afterCommit,
                        hasPublishedIdentity,
                        forceDeploy
                    )
                )
                {
                    Trace.WriteLine(
                        $"[BundledPluginDeployer] Synced bundled plugin {name} → {dest}"
                    );
                    deployed++;
                }
                else
                {
                    Trace.WriteLine($"[BundledPluginDeployer] {name} already up to date.");
                }
            }
            catch (Exception ex)
            {
                allPluginsSucceeded = false;
                Trace.WriteLine($"[BundledPluginDeployer] Failed to deploy {name}: {ex.Message}");
            }
        }

        if (forceDeploy && allPluginsSucceeded)
        {
            try
            {
                WriteBundleIdentity(destRoot, sourceIdentity);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deployment itself succeeded; a failed identity advance only costs a
                // force-redeploy on the next startup, so it must not abort bootstrap.
                Trace.WriteLine(
                    $"[BundledPluginDeployer] Failed to record bundle identity: {ex.Message}"
                );
            }
        }

        return deployed;
    }

    private static string? FindBundledPluginsDir()
    {
        var exeDir = AppContext.BaseDirectory;
        var candidate = Path.Join(exeDir, "Plugins");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static bool DeployPlugin(
        string source,
        string destRoot,
        string dest,
        string pluginName,
        Action<string, string> copyFile,
        Action<string>? afterCommit,
        bool hasPublishedIdentity,
        bool forceDeploy
    )
    {
        var scratchRoot = Path.Join(destRoot, ScratchDirectoryName);
        var pluginScratch = Path.Join(scratchRoot, pluginName);
        var transaction = new ManagedDirectoryTransaction(
            scratchRoot,
            ManagedDirectoryRecoveryMode.KeepPublished,
            useCrossProcessLock: false,
            cleanupAbandonedStages: true
        );
        transaction
            .RecoverAsync(pluginName, dest)
            .GetAwaiter()
            .GetResult();

        Fingerprints? sourceFingerprints = null;
        if (!forceDeploy && TryReadStamp(dest, out var stamp))
        {
            var sourceStat = stamp.SourceStat;
            var refreshStamp = false;
            var deploy = false;

            if (!hasPublishedIdentity)
            {
                sourceStat = ComputeStatDigest(source);
                if (!DigestsEqual(sourceStat, stamp.SourceStat))
                {
                    sourceFingerprints = ComputeFingerprints(source);
                    sourceStat = sourceFingerprints.Stat;
                    if (!DigestsEqual(sourceFingerprints.Content, stamp.Content))
                    {
                        deploy = true;
                    }
                    else
                    {
                        refreshStamp = true;
                    }
                }
            }

            if (!deploy)
            {
                var destStat = ComputeStatDigest(dest);
                if (!DigestsEqual(destStat, stamp.DestStat))
                {
                    var destFingerprints = ComputeFingerprints(dest);
                    destStat = destFingerprints.Stat;
                    if (!DigestsEqual(destFingerprints.Content, stamp.Content))
                    {
                        deploy = true;
                    }
                    else
                    {
                        refreshStamp = true;
                    }
                }

                if (!deploy)
                {
                    if (refreshStamp)
                    {
                        WriteStamp(dest, new DeploymentStamp(stamp.Content, sourceStat, destStat));
                    }

                    RemoveEmptyDirectory(pluginScratch);
                    RemoveEmptyDirectory(scratchRoot);
                    return false;
                }
            }
        }

        sourceFingerprints ??= ComputeFingerprints(source);
        var stage = transaction.CreateStageDirectory(pluginName);
        try
        {
            CopyDirectory(source, stage, copyFile);

            var stagedFingerprints = ComputeFingerprints(stage);
            if (!DigestsEqual(sourceFingerprints.Content, stagedFingerprints.Content))
            {
                throw new IOException("Bundled plugin changed while it was being staged.");
            }

            WriteStamp(
                stage,
                new DeploymentStamp(
                    sourceFingerprints.Content,
                    sourceFingerprints.Stat,
                    stagedFingerprints.Stat
                )
            );
            var commit = transaction
                .CommitAsync(pluginName, stage, dest)
                .GetAwaiter()
                .GetResult();
            try
            {
                try
                {
                    afterCommit?.Invoke(dest);

                    var destStat = ComputeStatDigest(dest);
                    if (!DigestsEqual(destStat, stagedFingerprints.Stat))
                    {
                        var committedFingerprints = ComputeFingerprints(dest);
                        if (
                            !DigestsEqual(
                                sourceFingerprints.Content,
                                committedFingerprints.Content
                            )
                        )
                        {
                            throw new IOException(
                                "Bundled plugin changed while it was being committed."
                            );
                        }

                        destStat = committedFingerprints.Stat;
                    }

                    WriteStamp(
                        dest,
                        new DeploymentStamp(
                            sourceFingerprints.Content,
                            sourceFingerprints.Stat,
                            destStat
                        )
                    );
                }
                finally
                {
                    // BundledPluginDeployer has always treated a published tree as
                    // authoritative even when its post-commit verification reports a
                    // failure; preserve that observable contract while still clearing
                    // the journal and backup transactionally.
                    commit.CompleteAsync().GetAwaiter().GetResult();
                }
            }
            finally
            {
                commit.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            return true;
        }
        finally
        {
            TryDeleteDirectory(stage);
            RemoveEmptyDirectory(pluginScratch);
            RemoveEmptyDirectory(scratchRoot);
        }
    }

    private static bool TryReadStamp(string dest, out DeploymentStamp stamp)
    {
        stamp = null!;
        if (!Directory.Exists(dest))
        {
            return false;
        }

        var stampPath = Path.Join(dest, StampFileName);
        if (!File.Exists(stampPath))
        {
            return false;
        }

        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(stampPath))
        {
            var separator = line.IndexOf('=');
            if (
                separator <= 0
                || !TryParseDigest(line[(separator + 1)..], out var digest)
                || !values.TryAdd(line[..separator], digest)
            )
            {
                return false;
            }
        }

        if (
            values.Count != 3
            || !values.TryGetValue("content", out var content)
            || !values.TryGetValue("sourceStat", out var sourceStat)
            || !values.TryGetValue("destStat", out var destStat)
        )
        {
            return false;
        }

        stamp = new DeploymentStamp(content, sourceStat, destStat);
        return true;
    }

    private static bool TryReadBundleIdentity(string root, out byte[] identity)
    {
        identity = [];
        var path = Path.Join(root, BundleIdentityFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        string value;
        try
        {
            value = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable marker degrades exactly like a missing or malformed one;
            // throwing here would take the whole plugin bootstrap stage down with it.
            Trace.WriteLine(
                $"[BundledPluginDeployer] Failed to read bundle identity at {path}: {ex.Message}"
            );
            return false;
        }

        return TryParseDigest(value, out identity);
    }

    private static void InvalidateInstalledIdentity(string destRoot)
    {
        try
        {
            File.Delete(Path.Join(destRoot, BundleIdentityFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(
                $"[BundledPluginDeployer] Failed to retract bundle identity: {ex.Message}"
            );
        }
    }

    private static bool TryParseDigest(string value, out byte[] digest)
    {
        digest = [];
        if (value.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        try
        {
            digest = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WriteStamp(string root, DeploymentStamp stamp)
    {
        File.WriteAllText(
            Path.Join(root, StampFileName),
            string.Join(
                Environment.NewLine,
                $"content={Convert.ToHexString(stamp.Content)}",
                $"sourceStat={Convert.ToHexString(stamp.SourceStat)}",
                $"destStat={Convert.ToHexString(stamp.DestStat)}"
            )
        );
    }

    private static void WriteBundleIdentity(string root, byte[] identity)
    {
        File.WriteAllText(
            Path.Join(root, BundleIdentityFileName),
            Convert.ToHexString(identity) + Environment.NewLine
        );
    }

    private static byte[] ComputeStatDigest(string root)
    {
        var files = GetFingerprintFiles(root);
        using var statDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendStatManifest(statDigest, files);
        return statDigest.GetHashAndReset();
    }

    private static Fingerprints ComputeFingerprints(string root)
    {
        var files = GetFingerprintFiles(root);
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stat = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendStatManifest(stat, files);

        foreach (var file in files)
        {
            content.AppendData([1]);
            content.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            content.AppendData([0]);

            using var stream = File.OpenRead(file.FullPath);
            content.AppendData(SHA256.HashData(stream));
        }

        return new Fingerprints(content.GetHashAndReset(), stat.GetHashAndReset());
    }

    internal static byte[] ComputeContentFingerprint(string root)
    {
        return ComputeFingerprints(root).Content;
    }

    private static FingerprintFile[] GetFingerprintFiles(string root)
    {
        return Directory
            .GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(file =>
                !string.Equals(Path.GetFileName(file), StampFileName, StringComparison.Ordinal)
            )
            .Select(file =>
            {
                var info = new FileInfo(file);
                return new FingerprintFile(
                    file,
                    NormalizeRelativePath(Path.GetRelativePath(root, file)),
                    info.Length,
                    info.LastWriteTimeUtc.Ticks
                );
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AppendStatManifest(
        IncrementalHash digest,
        IReadOnlyList<FingerprintFile> files
    )
    {
        Span<byte> stats = stackalloc byte[sizeof(long) * 2];
        foreach (var file in files)
        {
            digest.AppendData([1]);
            digest.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            digest.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(stats[..sizeof(long)], file.Length);
            BinaryPrimitives.WriteInt64LittleEndian(stats[sizeof(long)..], file.LastWriteTicks);
            digest.AppendData(stats);
        }
    }

    private static bool DigestsEqual(byte[] left, byte[] right)
    {
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
        return Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? normalized
            : normalized.Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static void CopyDirectory(
        string source,
        string destination,
        Action<string, string> copyFile
    )
    {
        foreach (
            var file in Directory
                .GetFiles(source)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
        )
        {
            if (string.Equals(Path.GetFileName(file), StampFileName, StringComparison.Ordinal))
            {
                continue;
            }

            copyFile(file, Path.Join(destination, Path.GetFileName(file)));
        }

        foreach (
            var subdirectory in Directory
                .GetDirectories(source)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
        )
        {
            var destinationSubdirectory = Path.Join(
                destination,
                Path.GetFileName(subdirectory)
            );
            Directory.CreateDirectory(destinationSubdirectory);
            CopyDirectory(subdirectory, destinationSubdirectory, copyFile);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[BundledPluginDeployer] Failed to clean deployment scratch {path}: {ex.Message}"
            );
        }
    }

    private static void RemoveEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[BundledPluginDeployer] Failed to clean deployment scratch {path}: {ex.Message}"
            );
        }
    }

    private sealed record DeploymentStamp(byte[] Content, byte[] SourceStat, byte[] DestStat);

    private sealed record Fingerprints(byte[] Content, byte[] Stat);

    private sealed record FingerprintFile(
        string FullPath,
        string RelativePath,
        long Length,
        long LastWriteTicks
    );
}
