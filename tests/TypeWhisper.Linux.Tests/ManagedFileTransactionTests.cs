// ReSharper disable MethodHasAsyncOverload -- synchronous File.ReadAll* is deliberate in these test assertions.
using System.Text;
using TypeWhisper.Linux.Services.ManagedArtifacts;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ManagedFileTransactionTests : IDisposable
{
    private const UnixFileMode Mode0600 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Mode0644 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _root = TestPaths.CreateTempDirectory("managed-file-transaction");

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_root);
        }
        catch
        {
            // Best-effort cleanup for interruption fixtures.
        }
    }

    [Fact]
    public async Task Install_and_remove_publish_only_the_recorded_exact_image()
    {
        var transaction = CreateTransaction();
        var spec = CreateSpec("strict", "# owned\ncurrent\n");

        var install = await transaction.InstallAsync(spec);

        Assert.True(install.Changed);
        Assert.Equal("# owned\ncurrent\n", File.ReadAllText(spec.DestinationPath));
        Assert.Equal(ManagedFileClassification.CurrentOwned, transaction.Probe(spec));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Mode0644, File.GetUnixFileMode(spec.DestinationPath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Join(StateRoot, "strict"))
            );
            Assert.Equal(
                Mode0600,
                File.GetUnixFileMode(Path.Join(StateRoot, "strict", "state.json"))
            );
        }

        var remove = await transaction.RemoveAsync(spec);

        Assert.True(remove.Changed);
        Assert.False(File.Exists(spec.DestinationPath));
        Assert.Equal(ManagedFileClassification.Absent, transaction.Probe(spec));
    }

    [Fact]
    public async Task Foreign_and_marker_customized_destinations_are_byte_preserved()
    {
        var transaction = CreateTransaction();
        var spec = CreateSpec("strict", "# owned\ncurrent\n");
        Directory.CreateDirectory(Path.GetDirectoryName(spec.DestinationPath)!);

        await File.WriteAllTextAsync(spec.DestinationPath, "foreign\n");
        var foreign = await transaction.InstallAsync(spec);
        Assert.Equal(ManagedFileClassification.Foreign, foreign.Classification);
        Assert.False(foreign.Changed);
        Assert.Equal("foreign\n", File.ReadAllText(spec.DestinationPath));

        await File.WriteAllTextAsync(spec.DestinationPath, "# owned\nuser customization\n");
        var customized = await transaction.InstallAsync(spec);
        var removal = await transaction.RemoveAsync(spec);

        Assert.Equal(ManagedFileClassification.CustomizedOwned, customized.Classification);
        Assert.Equal(ManagedFileClassification.CustomizedOwned, removal.Classification);
        Assert.False(customized.Changed);
        Assert.False(removal.Changed);
        Assert.Equal("# owned\nuser customization\n", File.ReadAllText(spec.DestinationPath));
    }

    [Fact]
    public async Task Exact_legacy_shape_is_adopted_but_nearby_shapes_are_not()
    {
        var transaction = CreateTransaction();
        var spec = CreateSpec("legacy", "# owned\ncurrent\n", "legacy exact\n");
        Directory.CreateDirectory(Path.GetDirectoryName(spec.DestinationPath)!);
        await File.WriteAllTextAsync(spec.DestinationPath, "legacy exact\n");

        var migrated = await transaction.InstallAsync(spec);

        Assert.Equal(ManagedFileClassification.StaleOwned, migrated.Classification);
        Assert.True(migrated.Changed);
        Assert.Equal("# owned\ncurrent\n", File.ReadAllText(spec.DestinationPath));

        await transaction.RemoveAsync(spec);
        await File.WriteAllTextAsync(spec.DestinationPath, "legacy exact\ncustomized\n");
        var refused = await transaction.InstallAsync(spec);

        Assert.Equal(ManagedFileClassification.Foreign, refused.Classification);
        Assert.Equal("legacy exact\ncustomized\n", File.ReadAllText(spec.DestinationPath));
    }

    [Fact]
    public async Task Symlink_and_directory_destinations_are_refused_without_touching_targets()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var transaction = CreateTransaction();
        var symlinkSpec = CreateSpec("symlink", "# owned\nnew\n");
        Directory.CreateDirectory(Path.GetDirectoryName(symlinkSpec.DestinationPath)!);
        var target = Path.Join(_root, "link-target");
        await File.WriteAllTextAsync(target, "target bytes\n");
        File.CreateSymbolicLink(symlinkSpec.DestinationPath, target);

        var symlinkResult = await transaction.InstallAsync(symlinkSpec);

        Assert.Equal(ManagedFileClassification.UnsupportedEntry, symlinkResult.Classification);
        Assert.Equal("target bytes\n", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(symlinkSpec.DestinationPath).LinkTarget);

        var directorySpec = CreateSpec("directory", "# owned\nnew\n");
        Directory.CreateDirectory(directorySpec.DestinationPath);
        var directoryResult = await transaction.InstallAsync(directorySpec);
        Assert.Equal(ManagedFileClassification.UnsupportedEntry, directoryResult.Classification);
        Assert.True(Directory.Exists(directorySpec.DestinationPath));
    }

    [Fact]
    public async Task Customized_recorded_publication_is_not_updated_or_removed_and_state_is_retained()
    {
        var transaction = CreateTransaction();
        var original = CreateSpec("customized", "# owned\nfirst\n");
        await transaction.InstallAsync(original);
        await File.AppendAllTextAsync(original.DestinationPath, "user edit\n");
        var update = original with { DesiredBytes = Bytes("# owned\nsecond\n") };

        var updateResult = await transaction.InstallAsync(update);
        var removeResult = await transaction.RemoveAsync(update);

        Assert.Equal(ManagedFileClassification.CustomizedOwned, updateResult.Classification);
        Assert.Equal(ManagedFileClassification.CustomizedOwned, removeResult.Classification);
        Assert.Equal("# owned\nfirst\nuser edit\n", File.ReadAllText(original.DestinationPath));
        Assert.True(File.Exists(Path.Join(StateRoot, "customized", "state.json")));
        Assert.True(File.Exists(Path.Join(StateRoot, "customized", "published.bin")));
    }

    [Fact]
    public async Task Backup_transform_restores_exact_foreign_bytes_and_mode()
    {
        var transaction = CreateTransaction();
        var spec = CreateSpec("shadow", "unused") with
        {
            ExistingPolicy = ManagedFileExistingPolicy.BackupTransformAndRestore,
            RemovalPolicy = ManagedFileRemovalPolicy.RestorePreimageIfUnchanged,
            BackupTransform = old => Bytes("# owned\n" + Encoding.UTF8.GetString(old.Span)),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(spec.DestinationPath)!);
        var foreign = Bytes("[Desktop Entry]\nName=User launcher\n");
        await File.WriteAllBytesAsync(spec.DestinationPath, foreign);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(spec.DestinationPath, Mode0600);
        }

        var install = await transaction.InstallAsync(spec);
        var remove = await transaction.RemoveAsync(spec);

        Assert.True(install.Changed);
        Assert.True(remove.Changed);
        Assert.Equal(foreign, File.ReadAllBytes(spec.DestinationPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Mode0600, File.GetUnixFileMode(spec.DestinationPath));
        }
    }

    [Theory]
    [InlineData((int)ManagedFileCheckpoint.InstallAfterJournal, false)]
    [InlineData((int)ManagedFileCheckpoint.InstallAfterPublish, true)]
    [InlineData((int)ManagedFileCheckpoint.InstallAfterState, true)]
    public async Task Interrupted_install_recovers_to_exact_old_or_new_state(
        int interruptionValue,
        bool expectsPublished
    )
    {
        var interruption = (ManagedFileCheckpoint)interruptionValue;
        var fired = false;
        var interrupted = new ManagedFileTransaction(
            StateRoot,
            (checkpoint, _) =>
            {
                if (fired || checkpoint != interruption)
                {
                    return Task.CompletedTask;
                }

                fired = true;
                throw new SimulatedCrashException();
            }
        );
        var spec = CreateSpec($"interrupt-{interruption}", "# owned\nnew\n");

        await Assert.ThrowsAsync<SimulatedCrashException>(() => interrupted.InstallAsync(spec));

        var recovered = CreateTransaction();
        var classification = await recovered.ProbeAsync(spec, CancellationToken.None);
        Assert.Equal(
            expectsPublished
                ? ManagedFileClassification.CurrentOwned
                : ManagedFileClassification.Absent,
            classification
        );
        Assert.Equal(expectsPublished, File.Exists(spec.DestinationPath));
        Assert.False(
            File.Exists(Path.Join(StateRoot, spec.ArtifactId, "pending.json"))
        );
    }

    [Theory]
    [InlineData((int)ManagedFileCheckpoint.RemoveAfterJournal, true)]
    [InlineData((int)ManagedFileCheckpoint.RemoveAfterPublish, false)]
    [InlineData((int)ManagedFileCheckpoint.RemoveAfterState, false)]
    public async Task Interrupted_remove_recovers_to_exact_old_or_removed_state(
        int interruptionValue,
        bool expectsPublished
    )
    {
        var interruption = (ManagedFileCheckpoint)interruptionValue;
        var spec = CreateSpec($"remove-{interruption}", "# owned\nnew\n");
        await CreateTransaction().InstallAsync(spec);
        var fired = false;
        var interrupted = new ManagedFileTransaction(
            StateRoot,
            (checkpoint, _) =>
            {
                if (fired || checkpoint != interruption)
                {
                    return Task.CompletedTask;
                }

                fired = true;
                throw new SimulatedCrashException();
            }
        );

        await Assert.ThrowsAsync<SimulatedCrashException>(() => interrupted.RemoveAsync(spec));

        var classification = await CreateTransaction().ProbeAsync(spec, CancellationToken.None);
        Assert.Equal(
            expectsPublished
                ? ManagedFileClassification.CurrentOwned
                : ManagedFileClassification.Absent,
            classification
        );
        Assert.Equal(expectsPublished, File.Exists(spec.DestinationPath));
    }

    [Fact]
    public async Task Recovery_preserves_external_edit_and_pending_images_on_conflict()
    {
        var spec = CreateSpec("recovery-conflict", "# owned\nnew\n");
        var interrupted = new ManagedFileTransaction(
            StateRoot,
            (checkpoint, _) =>
            {
                if (checkpoint != ManagedFileCheckpoint.InstallAfterJournal)
                {
                    return Task.CompletedTask;
                }

                File.WriteAllText(spec.DestinationPath, "external edit\n");
                throw new SimulatedCrashException();
            }
        );

        await Assert.ThrowsAsync<SimulatedCrashException>(() => interrupted.InstallAsync(spec));
        await Assert.ThrowsAsync<ManagedFileRecoveryConflictException>(() =>
            CreateTransaction().ProbeAsync(spec, CancellationToken.None)
        );

        Assert.Equal("external edit\n", File.ReadAllText(spec.DestinationPath));
        Assert.True(File.Exists(Path.Join(StateRoot, spec.ArtifactId, "pending.json")));
        Assert.True(File.Exists(Path.Join(StateRoot, spec.ArtifactId, "pending-new.bin")));
    }

    [Fact]
    public async Task Concurrent_operations_for_one_artifact_serialize_in_process()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSpec = CreateSpec("serialized", "# owned\nfirst\n");
        var secondSpec = firstSpec with { DesiredBytes = Bytes("# owned\nsecond\n") };
        var first = new ManagedFileTransaction(
            StateRoot,
            async (checkpoint, _) =>
            {
                if (checkpoint == ManagedFileCheckpoint.InstallAfterStage)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            }
        );

        var firstTask = first.InstallAsync(firstSpec);
        await entered.Task;
        var secondTask = CreateTransaction().InstallAsync(secondSpec);
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted);

        release.TrySetResult();
        await Task.WhenAll(firstTask, secondTask);

        Assert.Equal("# owned\nsecond\n", File.ReadAllText(firstSpec.DestinationPath));
        Assert.Equal(ManagedFileClassification.CurrentOwned, CreateTransaction().Probe(secondSpec));
    }

    private string StateRoot => Path.Join(_root, "state");

    private ManagedFileTransaction CreateTransaction()
    {
        return new ManagedFileTransaction(StateRoot);
    }

    private ManagedFileSpec CreateSpec(
        string artifactId,
        string desired,
        string? legacy = null
    )
    {
        var destination = Path.Join(_root, "destinations", $"{artifactId}.conf");
        return new ManagedFileSpec
        {
            ArtifactId = artifactId,
            DestinationPath = destination,
            DesiredBytes = Bytes(desired),
            CreateMode = Mode0644,
            OwnershipProbe = contents => contents.Span.StartsWith(Bytes("# owned\n")),
            LegacyOwnershipProbe = legacy is null
                ? null
                : contents => contents.Span.SequenceEqual(Bytes(legacy)),
        };
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class SimulatedCrashException : Exception;
}
