// ReSharper disable MethodHasAsyncOverload -- synchronous File IO is deliberate in these test arrange/assert steps.
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.ManagedArtifacts;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ManagedDirectoryTransactionTests : IDisposable
{
    private readonly string _root = TestPaths.CreateTempDirectory(
        "managed-directory-transaction"
    );

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_root);
    }

    [Fact]
    public async Task CommitAndComplete_ReplacesLiveTreeAndDeletesBackupAndJournal()
    {
        var live = Path.Join(_root, "plugins", "example");
        var state = Path.Join(_root, "plugins", ".transactions");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Join(live, "version.txt"), "old");
        var transaction = new ManagedDirectoryTransaction(state);
        var stage = transaction.CreateStageDirectory("example");
        File.WriteAllText(Path.Join(stage, "version.txt"), "new");

        await using (var commit = await transaction.CommitAsync("example", stage, live))
        {
            Assert.Equal("new", File.ReadAllText(Path.Join(live, "version.txt")));
            await commit.CompleteAsync();
        }

        Assert.Equal("new", File.ReadAllText(Path.Join(live, "version.txt")));
        Assert.False(Directory.Exists(Path.Join(state, "example", "backup")));
        Assert.False(File.Exists(Path.Join(state, "example", "pending.json")));
    }

    [Fact]
    public async Task RecoverAsync_InterruptedPublishedSwap_RestoresBackupAndMovesNewTreeAside()
    {
        var live = Path.Join(_root, "plugins", "example");
        var state = Path.Join(_root, "plugins", ".transactions");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Join(live, "version.txt"), "old");
        var transaction = new ManagedDirectoryTransaction(state);
        var stage = transaction.CreateStageDirectory("example");
        File.WriteAllText(Path.Join(stage, "version.txt"), "new");

        var interrupted = await transaction.CommitAsync("example", stage, live);
        Assert.Equal("new", File.ReadAllText(Path.Join(live, "version.txt")));
        await interrupted.DisposeAsync();

        await new ManagedDirectoryTransaction(state).RecoverAsync("example", live);

        Assert.Equal("old", File.ReadAllText(Path.Join(live, "version.txt")));
        Assert.False(Directory.Exists(Path.Join(state, "example", "backup")));
        Assert.False(File.Exists(Path.Join(state, "example", "pending.json")));
        var rejected = Assert.Single(
            Directory.GetDirectories(
                Path.Join(state, "example"),
                "rejected-*",
                SearchOption.TopDirectoryOnly
            )
        );
        Assert.Equal("new", File.ReadAllText(Path.Join(rejected, "version.txt")));
    }

    [Fact]
    public async Task RecoverAllAsync_ActivatedJournalKeepsPublishedTree()
    {
        var live = Path.Join(_root, "plugins", "example");
        var state = Path.Join(_root, "plugins", ".transactions");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Join(live, "version.txt"), "old");
        var transaction = new ManagedDirectoryTransaction(state);
        var stage = transaction.CreateStageDirectory("example");
        File.WriteAllText(Path.Join(stage, "version.txt"), "new");

        var commit = await transaction.CommitAsync("example", stage, live);
        // Simulate termination after activation was accepted but before cleanup by
        // writing the same durable state CompleteAsync uses, then retaining backup.
        var journalPath = Path.Join(state, "example", "pending.json");
        var pendingJournal = File.ReadAllText(journalPath);
        Assert.Contains("\"State\": 0", pendingJournal, StringComparison.Ordinal);
        var journal = pendingJournal.Replace(
            "\"State\": 0",
            "\"State\": 1",
            StringComparison.Ordinal
        );
        AtomicFileWrite.WriteAllText(journalPath, journal);
        await commit.DisposeAsync();

        await new ManagedDirectoryTransaction(state).RecoverAllAsync();

        Assert.Equal("new", File.ReadAllText(Path.Join(live, "version.txt")));
        Assert.False(Directory.Exists(Path.Join(state, "example", "backup")));
        Assert.False(File.Exists(journalPath));
    }
}
