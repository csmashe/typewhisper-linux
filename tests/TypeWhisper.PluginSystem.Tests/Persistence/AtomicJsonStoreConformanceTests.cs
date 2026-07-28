// ReSharper disable ArrangeObjectCreationWhenTypeNotEvident -- target-typed `new(...)` keeps the
// adapter matrix one row per line; naming the type on every row would wrap all fourteen.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

// AdapterPolicy is not IXunitSerializable, so Test Explorer lists the theory rather than its rows.
// The rows still all run, and ToString() names each one in the results.
#pragma warning disable xUnit1044

namespace TypeWhisper.PluginSystem.Tests.Persistence;

/// <summary>
///     The service adapters retain their domain tests; this matrix binds every migrated state
///     row to the shared transaction policy and runs the storage-level failure contract once.
///     Interrupted publication uses the deterministic staged hook rather than killing vstest.
/// </summary>
public sealed class AtomicJsonStoreConformanceTests
{
    public static TheoryData<AdapterPolicy> Adapters =>
        [
            new("settings", Backup: true, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("history", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("dictionary", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("profile", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("prompt-actions", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("snippets", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("error-log", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, false),
            new("linux-preferences", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("plugin-host-settings", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("watch-fingerprints", true, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("watch-history", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, false),
            new("file-memory", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
            new("openai-vector-memory", false, AtomicJsonCorruptFilePolicy.Throw, true),
            new("openai-api-key", false, AtomicJsonCorruptFilePolicy.PreserveAndReset, true),
        ];

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task ConcurrentUpdates_AcrossOneAndTwoInstances_PreserveAllMarkers(
        AdapterPolicy policy
    )
    {
        var directory = TestPaths.CreateTempDirectory($"AtomicJsonMatrix-{policy.Id}");
        var path = Path.Join(directory, "state.json");
        try
        {
            var first = new Adapter(path, policy);
            var second = new Adapter(path, policy);
            using var start = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, 32)
                .Select(index =>
                    Task.Run(() =>
                    {
                        // ReSharper disable once AccessToDisposedClosure -- every task is awaited
                        // below, inside the scope that owns the event.
                        start.Wait();
                        (index % 2 == 0 ? first : second).Add($"marker-{index}");
                    })
                )
                .ToArray();
            start.Set();
            await Task.WhenAll(tasks);

            var expected = Enumerable.Range(0, 32)
                .Select(index => $"marker-{index}")
                .Order()
                .ToArray();
            Assert.Equal(expected, first.Read().Order().ToArray());
            Assert.Equal(expected, second.Read().Order().ToArray());
            Assert.Equal(expected, new Adapter(path, policy).Read().Order().ToArray());
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void InterruptedOrFailedReplacement_RollsBackMemoryDiskAndEvent(
        AdapterPolicy policy
    )
    {
        var directory = TestPaths.CreateTempDirectory($"AtomicJsonFailure-{policy.Id}");
        var path = Path.Join(directory, "state.json");
        try
        {
            new Adapter(path, policy).Add("A");
            var before = File.ReadAllBytes(path);
            var staged = new ConcurrentBag<string>();
            var adapter = new Adapter(
                path,
                policy,
                (destination, json) =>
                    AtomicFileWrite.WriteAllText(
                        destination,
                        json,
                        sibling =>
                        {
                            staged.Add(sibling);
                            throw new InjectedReplacementException();
                        }
                    )
            );

            var exception = Record.Exception(() => adapter.Add("B"));

            Assert.Equal(policy.SurfacesFailure, exception is not null);
            Assert.Equal(["A"], adapter.Read().ToArray());
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(0, adapter.ChangeCount);
            Assert.All(staged, sibling => Assert.False(File.Exists(sibling)));

            new Adapter(path, policy).Add("C");
            Assert.Equal(["A", "C"], new Adapter(path, policy).Read().ToArray());
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void CorruptPrimary_FollowsDeclaredRecoveryPolicy(AdapterPolicy policy)
    {
        var directory = TestPaths.CreateTempDirectory($"AtomicJsonCorrupt-{policy.Id}");
        var path = Path.Join(directory, "state.json");
        var corrupt = "{ not valid"u8.ToArray();
        try
        {
            if (policy.Backup)
            {
                var seed = new Adapter(path, policy);
                seed.Add("A");
                seed.Add("B");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite
                    );
                }
                File.WriteAllBytes(path, corrupt);

                Assert.Equal(["A"], new Adapter(path, policy).Read().ToArray());
                Assert.Equal(["A"], Deserialize(File.ReadAllText(path)).ToArray());
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(
                        UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        File.GetUnixFileMode(path)
                    );
                }
            }
            else
            {
                File.WriteAllBytes(path, corrupt);
                var adapter = new Adapter(path, policy);
                if (policy.CorruptPolicy == AtomicJsonCorruptFilePolicy.Throw)
                {
                    Assert.Throws<JsonException>(() => adapter.Read());
                    Assert.Equal(corrupt, File.ReadAllBytes(path));
                }
                else
                {
                    Assert.Empty(adapter.Read());
                    Assert.Equal(corrupt, File.ReadAllBytes(path));
                    var preserved = Assert.Single(
                        Directory.EnumerateFiles(directory, "state.json.broken-*")
                    );
                    Assert.Equal(corrupt, File.ReadAllBytes(preserved));
                }
            }
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void SuccessfulReplacement_PreservesUnixPermission(AdapterPolicy policy)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = TestPaths.CreateTempDirectory($"AtomicJsonMode-{policy.Id}");
        var path = Path.Join(directory, "state.json");
        try
        {
            var adapter = new Adapter(path, policy);
            adapter.Add("A");
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);

            adapter.Add("B");

            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void RequiredBackupFailure_AbortsBeforePrimaryAndCache(AdapterPolicy policy)
    {
        if (!policy.Backup)
        {
            return;
        }

        var directory = TestPaths.CreateTempDirectory($"AtomicJsonBackup-{policy.Id}");
        var path = Path.Join(directory, "state.json");
        try
        {
            new Adapter(path, policy).Add("A");
            var before = File.ReadAllBytes(path);
            var adapter = new Adapter(
                path,
                policy,
                (destination, json) =>
                {
                    if (destination.EndsWith(".bak", StringComparison.Ordinal))
                    {
                        throw new IOException("Injected backup failure.");
                    }

                    AtomicFileWrite.WriteAllText(destination, json);
                }
            );

            Assert.Throws<IOException>(() => adapter.Add("B"));
            Assert.Equal(["A"], adapter.Read().ToArray());
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void UnreadablePrimary_NeverBecomesWritableDefaults(AdapterPolicy policy)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var directory = TestPaths.CreateTempDirectory($"AtomicJsonUnreadable-{policy.Id}");
        var path = Path.Join(directory, "state.json");
        File.WriteAllText(path, "[\"A\"]");
        var original = File.ReadAllBytes(path);
        var mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            var adapter = new Adapter(path, policy);

            Assert.ThrowsAny<Exception>(() => adapter.Read());
            _ = Record.Exception(() => adapter.Add("B"));
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
            Assert.Equal(original, File.ReadAllBytes(path));
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task StagedPaths_AreUniqueSiblingsAndCleanedAfterNormalFailures()
    {
        var directory = TestPaths.CreateTempDirectory("AtomicJsonUniqueStaging");
        var path = Path.Join(directory, "state.json");
        await File.WriteAllTextAsync(path, "A");
        var staged = new ConcurrentBag<string>();
        using var release = new ManualResetEventSlim();
        var entered = new CountdownEvent(8);
        try
        {
            var tasks = Enumerable.Range(0, 8)
                .Select(index =>
                    Task.Run(() =>
                        Assert.Throws<InjectedReplacementException>(() =>
                            AtomicFileWrite.WriteAllText(
                                path,
                                index.ToString(),
                                sibling =>
                                {
                                    staged.Add(sibling);
                                    entered.Signal();

                                    // ReSharper disable once AccessToDisposedClosure -- every task
                                    // is awaited below, inside the scope that owns the event.
                                    release.Wait();
                                    throw new InjectedReplacementException();
                                }
                            )
                        )
                    )
                )
                .ToArray();

            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(8, staged.Distinct(StringComparer.Ordinal).Count());
            Assert.All(staged, sibling => Assert.Equal(directory, Path.GetDirectoryName(sibling)));
            release.Set();
            await Task.WhenAll(tasks);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.Equal("A", await File.ReadAllTextAsync(path));
        }
        finally
        {
            release.Set();
            TestPaths.DeleteDirectory(directory);
        }
    }

    public sealed record AdapterPolicy(
        string Id,
        bool Backup,
        AtomicJsonCorruptFilePolicy CorruptPolicy,
        bool SurfacesFailure
    )
    {
        public override string ToString() => Id;
    }

    private sealed class Adapter
    {
        private readonly AdapterPolicy _policy;
        private readonly AtomicJsonStore<ImmutableArray<string>> _store;

        public Adapter(
            string path,
            AdapterPolicy policy,
            Action<string, string>? writer = null
        )
        {
            _policy = policy;
            var options = new AtomicJsonStoreOptions<ImmutableArray<string>>
            {
                BackupMode = policy.Backup
                    ? AtomicJsonBackupMode.LastKnownGood
                    : AtomicJsonBackupMode.None,
                CorruptFilePolicy = policy.CorruptPolicy,
                Deserialize = Deserialize,
            };
            _store = writer is null
                ? new AtomicJsonStore<ImmutableArray<string>>(
                    path,
                    static () => [],
                    options
                )
                : new AtomicJsonStore<ImmutableArray<string>>(
                    path,
                    static () => [],
                    options,
                    writer
                );
        }

        public int ChangeCount { get; private set; }

        public void Add(string marker)
        {
            try
            {
                _store.Update(current => current.Add(marker));
                ChangeCount++;
            }
            catch when (!_policy.SurfacesFailure)
            {
                // Mirrors the intentionally best-effort service boundary.
            }
        }

        public ImmutableArray<string> Read() => _store.Current;
    }

    private static ImmutableArray<string> Deserialize(string json)
    {
        var value = JsonSerializer.Deserialize<ImmutableArray<string>>(json);
        return value.IsDefault
            ? throw new JsonException("Conformance JSON deserialized to null.")
            : value;
    }

    private sealed class InjectedReplacementException : IOException;
}
