using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class BundledPluginDeployerTests
{
    private const string PluginName = "sample-plugin";
    private const string StampFileName = ".typewhisper-bundle.sha256";
    private const string BundleIdentityFileName = ".typewhisper-bundle-identity.sha256";
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
    public void ChangedPublishedIdentity_WithSameLengthsAndMtimes_ForceRedeploysAll()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-published-change");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            _ = CreatePlugin(sourceRoot, "second-plugin");
            var firstIdentity = WritePublishedIdentity(sourceRoot);
            Assert.Equal(2, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            var sourceMtime = File.GetLastWriteTimeUtc(sourceDll);
            var replacement = "current-v2";
            Assert.Equal(File.ReadAllBytes(sourceDll).Length, replacement.Length);
            File.WriteAllText(sourceDll, replacement);
            File.SetLastWriteTimeUtc(sourceDll, sourceMtime);
            var secondIdentity = WritePublishedIdentity(sourceRoot);
            Assert.NotEqual(firstIdentity, secondIdentity);

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(2, deployed);
            Assert.Equal(
                replacement,
                File.ReadAllText(Path.Join(destRoot, PluginName, "current.dll"))
            );
            Assert.Equal(secondIdentity, ReadInstalledIdentity(destRoot));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedSourceIdentity_UsesLegacyFingerprinting(bool malformed)
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-source-identity-fallback");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            _ = WritePublishedIdentity(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var identityPath = Path.Join(sourceRoot, BundleIdentityFileName);
            if (malformed)
            {
                File.WriteAllText(identityPath, "not-a-sha256");
            }
            else
            {
                File.Delete(identityPath);
            }

            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            File.WriteAllText(sourceDll, "current-v2");
            File.SetLastWriteTimeUtc(
                sourceDll,
                File.GetLastWriteTimeUtc(sourceDll).AddMinutes(5)
            );

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(1, deployed);
            Assert.Equal(
                "current-v2",
                File.ReadAllText(Path.Join(destRoot, PluginName, "current.dll"))
            );
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedDestinationIdentity_ForcesRedeployment(bool malformed)
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-destination-identity");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            _ = CreateBundle(sourceRoot);
            var sourceIdentity = WritePublishedIdentity(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var identityPath = Path.Join(destRoot, BundleIdentityFileName);
            if (malformed)
            {
                File.WriteAllText(identityPath, "not-a-sha256");
            }
            else
            {
                File.Delete(identityPath);
            }

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

            Assert.Equal(1, deployed);
            Assert.True(copyCount > 0);
            Assert.Equal(sourceIdentity, ReadInstalledIdentity(destRoot));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void PluginFailure_DoesNotAdvanceInstalledIdentity_AndRetries()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-identity-failure");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var firstPlugin = CreateBundle(sourceRoot);
            var failingPlugin = CreatePlugin(sourceRoot, "z-failing-plugin");
            var firstIdentity = WritePublishedIdentity(sourceRoot);
            Assert.Equal(2, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            File.WriteAllText(Path.Join(firstPlugin, "current.dll"), "current-v2");
            File.WriteAllText(Path.Join(failingPlugin, "current.dll"), "current-v2");
            var secondIdentity = WritePublishedIdentity(sourceRoot);
            var failingPrefix = failingPlugin + Path.DirectorySeparatorChar;

            var deployed = BundledPluginDeployer.DeployIfMissing(
                sourceRoot,
                destRoot,
                (source, destination) =>
                {
                    if (source.StartsWith(failingPrefix, StringComparison.Ordinal))
                    {
                        throw new IOException("Injected plugin copy failure.");
                    }

                    File.Copy(source, destination);
                }
            );

            Assert.Equal(1, deployed);
            Assert.Equal(firstIdentity, ReadInstalledIdentity(destRoot));
            Assert.Equal(
                "current-v2",
                File.ReadAllText(Path.Join(destRoot, PluginName, "current.dll"))
            );
            Assert.Equal(
                "current-v1",
                File.ReadAllText(Path.Join(destRoot, "z-failing-plugin", "current.dll"))
            );

            Assert.Equal(2, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            Assert.Equal(secondIdentity, ReadInstalledIdentity(destRoot));
            Assert.Equal(
                "current-v2",
                File.ReadAllText(Path.Join(destRoot, "z-failing-plugin", "current.dll"))
            );
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void VersionMismatch_PreservesUnbundledPluginDirectory()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-preserve-unbundled");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            _ = WritePublishedIdentity(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var userPlugin = Path.Join(destRoot, "user-installed-plugin");
            Directory.CreateDirectory(Path.Join(userPlugin, "data"));
            File.WriteAllText(Path.Join(userPlugin, "manifest.json"), "user manifest");
            File.WriteAllText(Path.Join(userPlugin, "data", "settings.json"), "user data");
            var userFiles = SnapshotFiles(userPlugin);

            File.WriteAllText(Path.Join(sourcePlugin, "current.dll"), "current-v2");
            _ = WritePublishedIdentity(sourceRoot);

            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            AssertSnapshotsEqual(userFiles, SnapshotFiles(userPlugin));
            Assert.Equal(
                "current-v2",
                File.ReadAllText(Path.Join(destRoot, PluginName, "current.dll"))
            );
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ContentFingerprint_IsContentBasedAndCreationOrderIndependent()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-fingerprint");
        try
        {
            var first = Path.Join(root, "first");
            var second = Path.Join(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Join(first, "a.dll"), "AAAA");
            File.WriteAllText(Path.Join(first, "b.dll"), "BBBB");
            File.WriteAllText(Path.Join(second, "b.dll"), "BBBB");
            File.WriteAllText(Path.Join(second, "a.dll"), "AAAA");

            var firstFingerprint = BundledPluginDeployer.ComputeContentFingerprint(first);
            var secondFingerprint = BundledPluginDeployer.ComputeContentFingerprint(second);
            Assert.Equal(firstFingerprint, secondFingerprint);

            var changedFile = Path.Join(first, "a.dll");
            var originalMtime = File.GetLastWriteTimeUtc(changedFile);
            File.WriteAllText(changedFile, "ZZZZ");
            File.SetLastWriteTimeUtc(changedFile, originalMtime);
            var changedFingerprint = BundledPluginDeployer.ComputeContentFingerprint(first);

            Assert.False(firstFingerprint.SequenceEqual(changedFingerprint));
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void UnmarkedDeployment_RetractsInstalledIdentity_AndMarkedRelaunchForceRedeploys()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-identity-retraction");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            _ = WritePublishedIdentity(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            // An unmarked (dev/legacy) run deploys a different tree and must retract
            // the marked build's attestation even though it cannot write a new one.
            File.Delete(Path.Join(sourceRoot, BundleIdentityFileName));
            var sourceDll = Path.Join(sourcePlugin, "current.dll");
            File.WriteAllText(sourceDll, "current-v2");
            File.SetLastWriteTimeUtc(
                sourceDll,
                File.GetLastWriteTimeUtc(sourceDll).AddMinutes(5)
            );
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));
            Assert.False(File.Exists(Path.Join(destRoot, BundleIdentityFileName)));

            // A marked relaunch finds no installed identity and force-redeploys even
            // though the destination content already matches the source.
            var identity = WritePublishedIdentity(sourceRoot);
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

            Assert.Equal(1, deployed);
            Assert.True(copyCount > 0);
            Assert.Equal(identity, ReadInstalledIdentity(destRoot));
            AssertNoScratch(destRoot);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void UnreadableSourceIdentity_UsesLegacyFingerprinting()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The fixture drives Unix file modes.
        }

        if (Environment.IsPrivilegedProcess)
        {
            return; // chmod 000 does not stop a privileged reader.
        }

        var root = TestPaths.CreateTempDirectory("bundled-plugin-unreadable-identity");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            var sourcePlugin = CreateBundle(sourceRoot);
            _ = WritePublishedIdentity(sourceRoot);
            Assert.Equal(1, BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot));

            var identityPath = Path.Join(sourceRoot, BundleIdentityFileName);
            File.SetUnixFileMode(identityPath, UnixFileMode.None);
            try
            {
                var sourceDll = Path.Join(sourcePlugin, "current.dll");
                File.WriteAllText(sourceDll, "current-v2");
                File.SetLastWriteTimeUtc(
                    sourceDll,
                    File.GetLastWriteTimeUtc(sourceDll).AddMinutes(5)
                );

                var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

                Assert.Equal(1, deployed);
                Assert.Equal(
                    "current-v2",
                    File.ReadAllText(Path.Join(destRoot, PluginName, "current.dll"))
                );
                AssertNoScratch(destRoot);
            }
            finally
            {
                File.SetUnixFileMode(
                    identityPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                );
            }
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void FailedIdentityAdvance_DoesNotAbortDeployment()
    {
        var root = TestPaths.CreateTempDirectory("bundled-plugin-identity-write-failure");
        try
        {
            var sourceRoot = Path.Join(root, "bundle");
            var destRoot = Path.Join(root, "installed");
            _ = CreateBundle(sourceRoot);
            _ = WritePublishedIdentity(sourceRoot);

            // A directory squatting on the marker path makes the identity advance
            // fail while the plugin deployment itself succeeds.
            Directory.CreateDirectory(Path.Join(destRoot, BundleIdentityFileName));

            var deployed = BundledPluginDeployer.DeployIfMissing(sourceRoot, destRoot);

            Assert.Equal(1, deployed);
            Assert.True(Directory.Exists(Path.Join(destRoot, BundleIdentityFileName)));
            Assert.Equal(
                "current-v1",
                File.ReadAllText(Path.Join(destRoot, PluginName, "current.dll"))
            );
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
        return CreatePlugin(sourceRoot, PluginName);
    }

    private static string CreatePlugin(string sourceRoot, string pluginName)
    {
        var plugin = Path.Join(sourceRoot, pluginName);
        Directory.CreateDirectory(Path.Join(plugin, "runtimes"));
        Directory.CreateDirectory(Path.Join(plugin, "empty"));
        File.WriteAllText(Path.Join(plugin, "manifest.json"), $"{{\"id\":\"{pluginName}\"}}");
        File.WriteAllText(Path.Join(plugin, "current.dll"), "current-v1");
        File.WriteAllText(Path.Join(plugin, "runtimes", "native.so"), "native-v1");
        return plugin;
    }

    private static string WritePublishedIdentity(string sourceRoot)
    {
        File.Delete(Path.Join(sourceRoot, BundleIdentityFileName));
        var identity = Convert.ToHexString(
            BundledPluginDeployer.ComputeContentFingerprint(sourceRoot)
        );
        File.WriteAllText(Path.Join(sourceRoot, BundleIdentityFileName), identity);
        return identity;
    }

    private static string ReadInstalledIdentity(string destRoot)
    {
        return File.ReadAllText(Path.Join(destRoot, BundleIdentityFileName)).TrimEnd('\r', '\n');
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
