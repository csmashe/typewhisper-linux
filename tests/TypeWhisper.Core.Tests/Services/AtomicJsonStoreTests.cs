using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

public sealed class AtomicJsonStoreTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    [Fact]
    public async Task Update_ConcurrentAcrossOneAndTwoInstances_PreservesEveryMarker()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreConcurrency");
        var path = Path.Join(directory, "state.json");
        try
        {
            var first = CreateStore(path);
            var second = CreateStore(path);
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 32)
                .Select(index =>
                    Task.Run(() =>
                    {
                        // ReSharper disable once AccessToDisposedClosure -- every task is awaited
                        // below, inside the scope that owns the event.
                        start.Wait();
                        var store = index % 2 == 0 ? first : second;
                        store.Update(current => current.Add($"marker-{index}"));
                    })
                )
                .ToArray();

            start.Set();
            await Task.WhenAll(tasks);

            var expected = Enumerable.Range(0, 32)
                .Select(index => $"marker-{index}")
                .Order()
                .ToArray();
            Assert.Equal(expected, first.Current.Order().ToArray());
            Assert.Equal(expected, second.Current.Order().ToArray());
            Assert.Equal(expected, CreateStore(path).Current.Order().ToArray());
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Update_StagedReplacementFailure_RollsBackMemoryAndDisk()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreReplacement");
        var path = Path.Join(directory, "state.json");
        try
        {
            CreateStore(path).Update(current => current.Add("A"));
            var before = File.ReadAllBytes(path);
            var stagedPaths = new ConcurrentBag<string>();
            var store = CreateStore(
                path,
                (destination, contents) =>
                    AtomicFileWrite.WriteAllText(
                        destination,
                        contents,
                        staged =>
                        {
                            stagedPaths.Add(staged);
                            throw new InjectedStagedWriteException();
                        }
                    )
            );

            Assert.Throws<InjectedStagedWriteException>(() =>
                store.Update(current => current.Add("B"))
            );

            Assert.Equal(["A"], store.Current.ToArray());
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.All(stagedPaths, staged => Assert.False(File.Exists(staged)));

            store = CreateStore(path);
            store.Update(current => current.Add("C"));
            Assert.Equal(["A", "C"], CreateStore(path).Current.ToArray());
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Current_WithBackup_RecoversLastKnownGoodAndRestoresPrimary()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreRecovery");
        var path = Path.Join(directory, "state.json");
        try
        {
            var store = CreateStore(path, backup: true);
            store.Update(current => current.Add("A"));
            store.Update(current => current.Add("B"));
            File.WriteAllText(path, "{ corrupt");

            var recovered = CreateStore(path, backup: true).Current;

            Assert.Equal(["A"], recovered.ToArray());
            Assert.Equal(["A"], CreateStore(path, backup: true).Current.ToArray());
            Assert.Equal(
                ["A"],
                JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!
            );
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Current_PreserveAndReset_PreservesExactCorruptBytesAndKeepsPrimary()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreCorrupt");
        var path = Path.Join(directory, "state.json");
        var corrupt = new byte[] { 0x7B, 0x22, 0x78, 0x22, 0x3A, 0xFF, 0x7D };
        try
        {
            File.WriteAllBytes(path, corrupt);
            var diagnostics = new List<AtomicJsonStoreDiagnostic>();
            var store = CreateStore(path, diagnostics: diagnostics.Add);

            Assert.Empty(store.Current);
            Assert.Equal(corrupt, File.ReadAllBytes(path));
            var preserved = Assert.Single(
                Directory.EnumerateFiles(directory, "state.json.broken-*")
            );
            Assert.Equal(corrupt, File.ReadAllBytes(preserved));
            Assert.Contains(
                diagnostics,
                diagnostic =>
                    diagnostic.Kind == AtomicJsonStoreDiagnosticKind.CorruptFilePreserved
                    && diagnostic.PreservedPath == preserved
            );
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Current_PreserveAndReset_PreservedCopyKeepsOriginalPermissions()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreCorruptMode");
        var path = Path.Join(directory, "state.json");
        try
        {
            File.WriteAllText(path, "{ corrupt");
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, ownerOnly);

            Assert.Empty(CreateStore(path).Current);

            var preserved = Assert.Single(
                Directory.EnumerateFiles(directory, "state.json.broken-*")
            );
            Assert.Equal(ownerOnly, File.GetUnixFileMode(preserved));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Update_SnapshotMutatedInPlaceAndReturned_IsCommitted()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreMutable");
        var path = Path.Join(directory, "state.json");
        try
        {
            var store = CreateMutableStore(path);
            store.Update(current =>
            {
                current.Add("A");
                return current;
            });
            store.Update(current =>
            {
                current.Add("B");
                return current;
            });

            Assert.Equal(["A", "B"], CreateMutableStore(path).Current);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Current_PreserveAndReset_CopiesTheSameCorruptContentOnlyOnce()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreCorruptOnce");
        var path = Path.Join(directory, "state.json");
        try
        {
            File.WriteAllText(path, "{ corrupt");

            // The corrupt primary stays put, so each of these runs into it again.
            var store = CreateStore(path);
            Assert.Empty(store.Current);
            Assert.Empty(store.Reload());
            Assert.Empty(CreateStore(path).Current);
            Assert.Empty(store.Current);

            Assert.Single(Directory.EnumerateFiles(directory, "state.json.broken-*"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Update_NoOpAfterCorruptReset_StillHealsThePrimary()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreHeal");
        var path = Path.Join(directory, "state.json");
        try
        {
            File.WriteAllText(path, "{ corrupt");
            var store = CreateStore(path);
            Assert.Empty(store.Current);

            // A no-op: the reset value is already what the store holds. The primary is still
            // corrupt though, so it has to be published anyway.
            store.Update(current => current);

            Assert.Equal("[]", File.ReadAllText(path).Trim());
            Assert.Empty(CreateStore(path).Current);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Update_MutatedInPlaceThenFailedWrite_DoesNotExposeTheLostChange()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreMutableRollback");
        var path = Path.Join(directory, "state.json");
        try
        {
            CreateMutableStore(path).Update(current =>
            {
                current.Add("A");
                return current;
            });

            var store = CreateMutableStore(
                path,
                (_, _) => throw new IOException("Injected write failure.")
            );
            Assert.Throws<IOException>(() =>
                store.Update(current =>
                {
                    current.Add("B");
                    return current;
                })
            );

            Assert.Equal(["A"], store.Current);
            Assert.Equal(["A"], CreateMutableStore(path).Current);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Current_WithBackup_LoadsRecoveredValueEvenWhenRepairingPrimaryFails()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreRepairFails");
        var path = Path.Join(directory, "state.json");
        try
        {
            var seed = CreateStore(path, backup: true);
            seed.Update(current => current.Add("A"));
            seed.Update(current => current.Add("B"));
            File.WriteAllText(path, "{ corrupt");

            // A good backup is on disk, but the primary cannot be rewritten from it.
            var store = CreateStore(
                path,
                (_, _) => throw new UnauthorizedAccessException("Injected repair failure."),
                backup: true
            );

            Assert.Equal(["A"], store.Current.ToArray());
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Current_ThrowPolicy_LeavesCorruptPrimaryUnchanged()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreThrow");
        var path = Path.Join(directory, "state.json");
        const string corrupt = "{ invalid";
        try
        {
            File.WriteAllText(path, corrupt);
            var store = CreateStore(path, corruptPolicy: AtomicJsonCorruptFilePolicy.Throw);

            Assert.Throws<JsonException>(() => _ = store.Current);
            Assert.Equal(corrupt, File.ReadAllText(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.broken-*"));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Update_BackupFailure_LeavesPrimaryAndMemoryUnchanged()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreBackupFailure");
        var path = Path.Join(directory, "state.json");
        try
        {
            var seed = CreateStore(path, backup: true);
            seed.Update(current => current.Add("A"));
            var before = File.ReadAllBytes(path);
            var store = CreateStore(
                path,
                (destination, contents) =>
                {
                    if (destination.EndsWith(".bak", StringComparison.Ordinal))
                    {
                        throw new IOException("Injected backup failure.");
                    }

                    AtomicFileWrite.WriteAllText(destination, contents);
                },
                backup: true
            );

            Assert.Throws<IOException>(() => store.Update(current => current.Add("B")));
            Assert.Equal(["A"], store.Current.ToArray());
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Update_PreservesExistingUnixMode()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreMode");
        var path = Path.Join(directory, "state.json");
        try
        {
            var store = CreateStore(path);
            store.Update(current => current.Add("A"));
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);

            store.Update(current => current.Add("B"));

            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Update_UnreadablePrimary_DoesNotPublishWritableDefaults()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var directory = TestPaths.CreateTempDirectory("AtomicJsonStoreUnreadable");
        var path = Path.Join(directory, "state.json");
        File.WriteAllText(path, "[\"A\"]");
        var originalMode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            var store = CreateStore(path);

            Assert.ThrowsAny<Exception>(() =>
                store.Update(current => current.Add("B"))
            );
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
            Assert.Equal("[\"A\"]", File.ReadAllText(path));
            TestPaths.DeleteDirectory(directory);
        }
    }

    /// <summary>
    ///     A mutable <c>T</c> is what the public plugin store contract allows, and it is the case
    ///     object equality cannot decide: the callback mutates the snapshot it was handed.
    /// </summary>
    private static AtomicJsonStore<List<string>> CreateMutableStore(
        string path,
        Action<string, string>? writer = null
    )
    {
        var options = new AtomicJsonStoreOptions<List<string>> { JsonOptions = s_jsonOptions };
        return writer is null
            ? new AtomicJsonStore<List<string>>(path, static () => [], options)
            : new AtomicJsonStore<List<string>>(path, static () => [], options, writer);
    }

    private static AtomicJsonStore<ImmutableArray<string>> CreateStore(
        string path,
        Action<string, string>? writer = null,
        bool backup = false,
        AtomicJsonCorruptFilePolicy corruptPolicy =
            AtomicJsonCorruptFilePolicy.PreserveAndReset,
        Action<AtomicJsonStoreDiagnostic>? diagnostics = null
    )
    {
        var options = new AtomicJsonStoreOptions<ImmutableArray<string>>
        {
            JsonOptions = s_jsonOptions,
            BackupMode = backup
                ? AtomicJsonBackupMode.LastKnownGood
                : AtomicJsonBackupMode.None,
            CorruptFilePolicy = corruptPolicy,
            Diagnostic = diagnostics,
            Deserialize = json =>
            {
                var value = JsonSerializer.Deserialize<ImmutableArray<string>>(
                    json,
                    s_jsonOptions
                );
                return value.IsDefault
                    ? throw new JsonException("State JSON deserialized to null.")
                    : value;
            },
        };
        return writer is null
            ? new AtomicJsonStore<ImmutableArray<string>>(path, static () => [], options)
            : new AtomicJsonStore<ImmutableArray<string>>(
                path,
                static () => [],
                options,
                writer
            );
    }
}
