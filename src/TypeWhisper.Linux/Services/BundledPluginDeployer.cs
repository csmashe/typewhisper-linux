using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Core;

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
    private const string ScratchDirectoryName = ".typewhisper-deploy";
    private const string BackupDirectoryName = "backup";

    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the count of synced plugins for callers that want it; the current caller ignores it.
    public static int DeployIfMissing()
    {
        var source = FindBundledPluginsDir();
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

                if (DeployPlugin(pluginDir, destRoot, dest, name, copyFile, afterCommit))
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
                Trace.WriteLine($"[BundledPluginDeployer] Failed to deploy {name}: {ex.Message}");
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
        Action<string>? afterCommit
    )
    {
        var scratchRoot = Path.Join(destRoot, ScratchDirectoryName);
        var pluginScratch = Path.Join(scratchRoot, pluginName);
        var backup = Path.Join(pluginScratch, BackupDirectoryName);
        RecoverInterruptedDeployment(dest, pluginScratch, backup);

        Fingerprints? sourceFingerprints = null;
        if (TryReadStamp(dest, out var stamp))
        {
            var sourceStat = ComputeStatDigest(source);
            var refreshStamp = false;
            var deploy = false;

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
        Directory.CreateDirectory(pluginScratch);
        var stage = CreateStageDirectory(pluginScratch);
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
            CommitStage(stage, dest, backup);
            afterCommit?.Invoke(dest);

            var destStat = ComputeStatDigest(dest);
            if (!DigestsEqual(destStat, stagedFingerprints.Stat))
            {
                var committedFingerprints = ComputeFingerprints(dest);
                if (!DigestsEqual(sourceFingerprints.Content, committedFingerprints.Content))
                {
                    throw new IOException("Bundled plugin changed while it was being committed.");
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
            TryDeleteDirectory(backup);
            return true;
        }
        finally
        {
            TryDeleteDirectory(stage);
            RemoveEmptyDirectory(pluginScratch);
            RemoveEmptyDirectory(scratchRoot);
        }
    }

    private static void RecoverInterruptedDeployment(
        string dest,
        string pluginScratch,
        string backup
    )
    {
        if (!Directory.Exists(pluginScratch))
        {
            return;
        }

        foreach (
            var abandonedStage in Directory
                .GetDirectories(pluginScratch, "stage-*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
        )
        {
            Directory.Delete(abandonedStage, recursive: true);
        }

        if (Directory.Exists(backup))
        {
            if (Directory.Exists(dest))
            {
                Directory.Delete(backup, recursive: true);
            }
            else
            {
                Directory.Move(backup, dest);
            }
        }
    }

    private static string CreateStageDirectory(string pluginScratch)
    {
        string stage;
        do
        {
            stage = Path.Join(pluginScratch, $"stage-{Guid.NewGuid():N}");
        } while (Directory.Exists(stage) || File.Exists(stage));

        Directory.CreateDirectory(stage);
        return stage;
    }

    private static void CommitStage(string stage, string dest, string backup)
    {
        if (!Directory.Exists(dest))
        {
            Directory.Move(stage, dest);
            return;
        }

        Directory.Move(dest, backup);
        try
        {
            Directory.Move(stage, dest);
        }
        catch (Exception commitException)
        {
            try
            {
                Directory.Move(backup, dest);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Failed to commit the bundled plugin and restore its previous deployment.",
                    commitException,
                    rollbackException
                );
            }

            throw;
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
