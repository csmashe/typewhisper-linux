using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class BundledPluginDeployerTests
{
    private const string PluginName = "sample-plugin";
    private const string StampFileName = ".typewhisper-bundle.sha256";
    private const string ScratchDirectoryName = ".typewhisper-deploy";

    [Fact]
    public void DeployIfMissing_MissingDestination_DeploysCompleteTreeAndStamp()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-fresh");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            var destPlugin = Path.Join(destRoot, PluginName);

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(1, deployed);
            AssertSourceFilesMatch(sourcePlugin, destPlugin);
            Assert.True(Directory.Exists(Path.Join(destPlugin, "empty")));
            Assert.True(File.Exists(Path.Join(destPlugin, StampFileName)));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_MatchingStamp_IsNoOpWithoutCopyOrRewrite()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-current");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            _ = CreateBundle(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var destPlugin = Path.Join(destRoot, PluginName);
            var livePath = Path.Join(destPlugin, "current.dll");
            var stampPath = Path.Join(destPlugin, StampFileName);
            var liveBefore = File.ReadAllBytes(livePath);
            var stampBefore = File.ReadAllBytes(stampPath);
            var copyCount = 0;

            var deployed = BundledPluginDeployer.DeployIfMissing(
                sourceRoot,
                destRoot,
                (source, destination) =>
                {
                    copyCount++;
                    File.Copy(source, destination);
                }
            );

            Assert.Equal(0, deployed);
            Assert.Equal(0, copyCount);
            Assert.Equal(liveBefore, File.ReadAllBytes(livePath));
            Assert.Equal(stampBefore, File.ReadAllBytes(stampPath));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_InvalidOrMissingStamp_RedeploysAndRestoresStamp()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-invalid-stamp");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var destPlugin = Path.Join(destRoot, PluginName);
            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            var destDll = Path.Join(destPlugin, "current.dll");
            var stampPath = Path.Join(destPlugin, StampFileName);
            var validStamp = File.ReadAllText(stampPath);
            AssertStructuredStamp(validStamp);

            File.WriteAllText(stampPath, "not-a-fingerprint");

            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            Assert.Equal(File.ReadAllBytes(sourceDll), File.ReadAllBytes(destDll));
            Assert.Equal(validStamp, File.ReadAllText(stampPath));
            AssertNoScratch(destRoot);

            File.WriteAllText(stampPath, new string('z', 64));

            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            Assert.Equal(File.ReadAllBytes(sourceDll), File.ReadAllBytes(destDll));
            Assert.Equal(validStamp, File.ReadAllText(stampPath));
            AssertNoScratch(destRoot);

            File.Delete(stampPath);

            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            Assert.Equal(File.ReadAllBytes(sourceDll), File.ReadAllBytes(destDll));
            Assert.Equal(validStamp, File.ReadAllText(stampPath));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_SteadyState_DoesNotReadFileContents()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-stat-gated");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            var destDll = Path.Join(destRoot, PluginName, "current.dll");
            var sourceMtime = File.GetLastWriteTimeUtc(sourceDll);
            var destMtime = File.GetLastWriteTimeUtc(destDll);
            var sourceReplacement = "source-v2!"u8.ToArray();
            var destReplacement = "dest-dmg!!"u8.ToArray();
            Assert.Equal(File.ReadAllBytes(sourceDll).Length, sourceReplacement.Length);
            Assert.Equal(File.ReadAllBytes(destDll).Length, destReplacement.Length);

            File.WriteAllBytes(sourceDll, sourceReplacement);
            File.SetLastWriteTimeUtc(sourceDll, sourceMtime);
            File.WriteAllBytes(destDll, destReplacement);
            File.SetLastWriteTimeUtc(destDll, destMtime);

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(0, deployed);
            Assert.Equal(destReplacement, File.ReadAllBytes(destDll));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_TouchedButIdenticalContent_RefreshesStampWithoutRedeploy()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-touched-source");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            var destPlugin = Path.Join(destRoot, PluginName);
            var stampPath = Path.Join(destPlugin, StampFileName);
            var payloadBefore = SnapshotPayloadFiles(destPlugin);
            var stampBefore = File.ReadAllText(stampPath);
            File.SetLastWriteTimeUtc(
                sourceDll,
                File.GetLastWriteTimeUtc(sourceDll).AddMinutes(5)
            );

            Assert.Equal(0, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            AssertSnapshotsEqual(payloadBefore, SnapshotPayloadFiles(destPlugin));
            var refreshedStamp = File.ReadAllText(stampPath);
            Assert.NotEqual(stampBefore, refreshedStamp);

            Assert.Equal(0, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            AssertSnapshotsEqual(payloadBefore, SnapshotPayloadFiles(destPlugin));
            Assert.Equal(refreshedStamp, File.ReadAllText(stampPath));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_ValidStampButDamagedDestination_RepairsFromBundle()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-damaged-dest");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var destPlugin = Path.Join(destRoot, PluginName);
            var stampPath = Path.Join(destPlugin, StampFileName);
            var validStamp = File.ReadAllText(stampPath);
            var destDll = Path.Join(destPlugin, "current.dll");

            // Same-length, older-mtime damage with the stamp still valid: only a content
            // re-hash of the destination can catch it.
            File.WriteAllText(destDll, "corrupted!");
            Assert.Equal(
                File.ReadAllBytes(Path.Join(sourcePlugin, "current.dll")).Length,
                File.ReadAllBytes(destDll).Length
            );
            File.SetLastWriteTimeUtc(destDll, File.GetLastWriteTimeUtc(destDll).AddMinutes(-5));

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(1, deployed);
            Assert.Equal("current-v1", File.ReadAllText(destDll));
            Assert.Equal(validStamp, File.ReadAllText(stampPath));
            AssertSourceFilesMatch(sourcePlugin, destPlugin);
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_SameLengthContentWithOlderMtime_Redeploys()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-content-change");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            File.WriteAllText(sourceDll, "AAAA");
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var destDll = Path.Join(destRoot, PluginName, "current.dll");
            File.WriteAllText(sourceDll, "BBBB");
            File.SetLastWriteTimeUtc(
                sourceDll,
                File.GetLastWriteTimeUtc(destDll).AddMinutes(-5)
            );
            Assert.True(File.GetLastWriteTimeUtc(sourceDll) <= File.GetLastWriteTimeUtc(destDll));

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(1, deployed);
            Assert.Equal("BBBB", File.ReadAllText(destDll));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_RemovedSourceFile_PrunesDestinationOnlyFile()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-prune");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            var sourceObsolete = Path.Join(sourcePlugin, "runtimes", "obsolete.dll");
            File.WriteAllText(sourceObsolete, "obsolete");
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var destPlugin = Path.Join(destRoot, PluginName);
            var destObsolete = Path.Join(destPlugin, "runtimes", "obsolete.dll");
            Assert.True(File.Exists(destObsolete));
            File.Delete(sourceObsolete);

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(1, deployed);
            AssertSourceFilesMatch(sourcePlugin, destPlugin);
            Assert.False(File.Exists(destObsolete));
            Assert.Equal("current-v1", File.ReadAllText(Path.Join(destPlugin, "current.dll")));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_CopyFailure_PreservesCompletePriorDeployment()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-copy-failure");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            File.WriteAllText(Path.Join(sourcePlugin, "z-fail.dll"), "v1-failure-target");
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var destPlugin = Path.Join(destRoot, PluginName);
            var deployedBefore = SnapshotFiles(destPlugin);
            File.WriteAllText(Path.Join(sourcePlugin, "current.dll"), "current-v2");
            File.WriteAllText(Path.Join(sourcePlugin, "a-v2-only.dll"), "new-v2");
            var copiedBeforeFailure = 0;

            var deployed = BundledPluginDeployer.DeployIfMissing(
                sourceRoot,
                destRoot,
                (source, destination) =>
                {
                    if (string.Equals(
                            Path.GetFileName(source),
                            "z-fail.dll",
                            StringComparison.Ordinal
                        ))
                    {
                        throw new IOException("Injected staging copy failure.");
                    }

                    File.Copy(source, destination);
                    copiedBeforeFailure++;
                }
            );

            Assert.Equal(0, deployed);
            Assert.True(copiedBeforeFailure > 0);
            AssertSnapshotsEqual(deployedBefore, SnapshotFiles(destPlugin));
            Assert.Equal("current-v1", File.ReadAllText(Path.Join(destPlugin, "current.dll")));
            Assert.False(File.Exists(Path.Join(destPlugin, "a-v2-only.dll")));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_InterruptedCommit_RestoresBackupAndCleansScratch()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-interrupted-commit");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            _ = CreateBundle(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var destPlugin = Path.Join(destRoot, PluginName);
            var deployedBefore = SnapshotFiles(destPlugin);

            var pluginScratch = Path.Join(destRoot, ScratchDirectoryName, PluginName);
            Directory.CreateDirectory(pluginScratch);
            Directory.Move(destPlugin, Path.Join(pluginScratch, "backup"));
            var abandonedStage = Path.Join(pluginScratch, "stage-deadbeef");
            Directory.CreateDirectory(abandonedStage);
            File.WriteAllText(Path.Join(abandonedStage, "junk.tmp"), "interrupted-copy");
            Assert.False(Directory.Exists(destPlugin));

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(0, deployed);
            AssertSnapshotsEqual(deployedBefore, SnapshotFiles(destPlugin));
            Assert.True(File.Exists(Path.Join(destPlugin, StampFileName)));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void DeployIfMissing_DestinationMutatedDuringCommit_DoesNotBlessAndRedeploys()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-commit-race");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            var destDll = Path.Join(destRoot, PluginName, "current.dll");
            File.WriteAllText(sourceDll, "current-v2");

            // Corrupt dest after CommitStage but before the stamp is finalized; a distinct
            // mtime keeps it stat-detectable, so finalize must not bless it.
            var corrupted = 0;
            var deployed = BundledPluginDeployer.DeployIfMissing(
                sourceRoot,
                destRoot,
                copyFile: null,
                afterCommit: _ =>
                {
                    if (corrupted++ > 0)
                    {
                        return;
                    }

                    File.WriteAllText(destDll, "wrecked-v2");
                    File.SetLastWriteTimeUtc(
                        destDll,
                        File.GetLastWriteTimeUtc(destDll).AddMinutes(-5)
                    );
                }
            );

            Assert.Equal(0, deployed);
            Assert.Equal("wrecked-v2", File.ReadAllText(destDll));

            var repaired = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(1, repaired);
            Assert.Equal("current-v2", File.ReadAllText(destDll));
            AssertSourceFilesMatch(sourcePlugin, Path.Join(destRoot, PluginName));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    private static string CreateBundle(string sourceRoot)
    {
        var plugin = Path.Join(sourceRoot, PluginName);
        Directory.CreateDirectory(Path.Join(plugin, "runtimes"));
        Directory.CreateDirectory(Path.Join(plugin, "empty"));
        File.WriteAllText(Path.Join(plugin, "manifest.json"), "{\"id\":\"sample-plugin\"}");
        File.WriteAllText(Path.Join(plugin, "current.dll"), "current-v1");
        File.WriteAllText(Path.Join(plugin, "runtimes", "native.so"), "native-v1");
        return plugin;
    }

    private static void AssertSourceFilesMatch(string sourcePlugin, string destPlugin)
    {
        var sourceFiles = SnapshotFiles(sourcePlugin);
        var destFiles = SnapshotPayloadFiles(destPlugin);
        AssertSnapshotsEqual(sourceFiles, destFiles);
    }

    private static Dictionary<string, byte[]> SnapshotPayloadFiles(string plugin)
    {
        return SnapshotFiles(plugin)
            .Where(entry => !string.Equals(entry.Key, StampFileName, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string root)
    {
        return Directory
            .GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal
            );
    }

    private static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual
    )
    {
        Assert.Equal(
            expected.Keys.OrderBy(path => path, StringComparer.Ordinal),
            actual.Keys.OrderBy(path => path, StringComparer.Ordinal)
        );
        foreach (var path in expected.Keys)
        {
            Assert.Equal(expected[path], actual[path]);
        }
    }

    private static void AssertStructuredStamp(string stamp)
    {
        var lines = stamp.Split(Environment.NewLine);
        Assert.Collection(
            lines,
            line => AssertStampDigest(line, "content"),
            line => AssertStampDigest(line, "sourceStat"),
            line => AssertStampDigest(line, "destStat")
        );
    }

    private static void AssertStampDigest(string line, string key)
    {
        var prefix = $"{key}=";
        Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        var value = line[prefix.Length..];
        Assert.Equal(64, value.Length);
        Assert.All(value, character => Assert.True(Uri.IsHexDigit(character)));
    }

    private static void AssertNoScratch(string destRoot)
    {
        Assert.False(Directory.Exists(Path.Join(destRoot, ScratchDirectoryName)));
    }
}
