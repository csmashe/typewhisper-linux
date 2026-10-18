using System.Text.Json;
using TypeWhisper.Core.Services;
using TypeWhisper.Plugin.FileMemory;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class FileMemoryPluginTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.FileMemoryPluginTests"
    );

    private string MemoryPath => Path.Join(_tempDir, "memories.json");

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            /* best effort */
        }
    }

    [Fact]
    public async Task Store_ThenNewPluginInstance_RoundTripsFromDisk()
    {
        using (var writer = await CreatePluginAsync())
        {
            await writer.StoreAsync("Uses Linux");
        }

        using var reader = await CreatePluginAsync();
        var entries = await reader.GetAllAsync();

        Assert.Equal(["Uses Linux"], entries);
    }

    [Fact]
    public async Task Store_IdenticalContentTwice_KeepsOneEntry()
    {
        using var plugin = await CreatePluginAsync();

        await plugin.StoreAsync("Prefers dark mode");
        await plugin.StoreAsync("Prefers dark mode");

        Assert.Equal(["Prefers dark mode"], await plugin.GetAllAsync());
    }

    [Fact]
    public async Task CorruptFile_IsPreservedBeforeFreshStoreIsSaved()
    {
        var originalBytes = "{ not valid json"u8.ToArray();
        await File.WriteAllBytesAsync(MemoryPath, originalBytes);
        using var plugin = await CreatePluginAsync();

        Assert.Empty(await plugin.GetAllAsync());
        var brokenPath = Assert.Single(
            Directory.EnumerateFiles(_tempDir, "memories.json.broken-*")
        );
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(brokenPath));

        await plugin.StoreAsync("Recovered memory");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(MemoryPath));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(
            "Recovered memory",
            document.RootElement[0].GetProperty("Content").GetString()
        );
    }

    [Fact]
    public async Task CanceledInitialLoad_DoesNotPoisonCache()
    {
        using (var writer = await CreatePluginAsync())
        {
            await writer.StoreAsync("Original memory");
        }

        using var reader = await CreatePluginAsync();
        using var cancellation = new CancellationTokenSource();
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel is deliberate; the token must be tripped before the call below.
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.GetAllAsync(cancellation.Token)
        );
        // ReSharper disable once MethodSupportsCancellation -- this fresh, uncancelled load is meant to succeed; passing a token would defeat the assertion.
        Assert.Equal(["Original memory"], await reader.GetAllAsync());
    }

    [Fact]
    public async Task FailedSave_LeavesCacheAndFileUnchangedWithoutTempFiles()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            // Root can bypass directory write permissions, so chmod cannot force this failure.
            return;
        }

        using var plugin = await CreatePluginAsync();
        await plugin.StoreAsync("Original memory");
        var originalBytes = await File.ReadAllBytesAsync(MemoryPath);
        var originalMode = File.GetUnixFileMode(_tempDir);

        try
        {
            File.SetUnixFileMode(
                _tempDir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
            );

            await Assert.ThrowsAnyAsync<Exception>(() => plugin.StoreAsync("New memory"));

            Assert.Equal(["Original memory"], await plugin.GetAllAsync());
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(MemoryPath));
            Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp"));
        }
        finally
        {
            File.SetUnixFileMode(_tempDir, originalMode);
        }
    }

    [Fact]
    public async Task UnreadableFile_SurfacesAndRefusesToOverwrite()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            // Root can read a mode-000 file, so this cannot exercise the unreadable-file path.
            return;
        }

        var originalBytes = "[{\"Content\":\"Original memory\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}]"u8
            .ToArray();
        await File.WriteAllBytesAsync(MemoryPath, originalBytes);
        var originalMode = File.GetUnixFileMode(MemoryPath);

        try
        {
            File.SetUnixFileMode(MemoryPath, UnixFileMode.None);
            using var plugin = await CreatePluginAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => plugin.GetAllAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => plugin.StoreAsync("New memory"));
            Assert.True(File.Exists(MemoryPath));
        }
        finally
        {
            File.SetUnixFileMode(MemoryPath, originalMode);
        }

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(MemoryPath));
    }

    [Fact]
    public async Task Search_RanksSharedWordsAndIgnoresStopWords()
    {
        using var plugin = await CreatePluginAsync();
        await plugin.StoreAsync("Project Aurora uses British English");
        await plugin.StoreAsync("Aurora ships in March");
        await plugin.StoreAsync("The cat is on the mat");

        var results = await plugin.SearchAsync(
            "Which spelling does the Aurora project use, British or American?"
        );

        Assert.Equal(["Project Aurora uses British English", "Aurora ships in March"], results);
    }

    [Fact]
    public async Task Search_PhraseMatchOutranksMoreSharedWords()
    {
        using var plugin = await CreatePluginAsync();
        await plugin.StoreAsync("dark mode editor theme settings preference");
        await plugin.StoreAsync("Chris prefers dark mode");

        var results = await plugin.SearchAsync("prefers dark mode");

        Assert.Equal("Chris prefers dark mode", results[0]);
    }

    [Fact]
    public async Task Search_ReturnsNothingWithoutASharedWordAndHonorsTheLimit()
    {
        using var plugin = await CreatePluginAsync();
        for (var i = 0; i < 8; i++)
        {
            await plugin.StoreAsync($"Linux fact number {i}");
        }

        Assert.Empty(await plugin.SearchAsync("the and of"));
        Assert.Equal(5, (await plugin.SearchAsync("Linux")).Count);
        Assert.Equal(2, (await plugin.SearchAsync("Linux", 2)).Count);
    }

    [Fact]
    public async Task Collection_LegacyEntriesGetStableIdsWithoutAWrite()
    {
        var legacy =
            "[{\"Content\":\"Uses Fedora\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}]"u8.ToArray();
        await File.WriteAllBytesAsync(MemoryPath, legacy);
        using var plugin = await CreateFileMemoryAsync();

        var first = Assert.Single(await GetItemsAsync(plugin));
        var second = Assert.Single(await GetItemsAsync(plugin));

        Assert.Equal("Uses Fedora", first.Values["content"]);
        Assert.Equal(first.Values["__id"], second.Values["__id"]);
        Assert.Equal(legacy, await File.ReadAllBytesAsync(MemoryPath));
    }

    [Fact]
    public async Task Collection_SaveEditsRemovesAndAddsInEditorOrder()
    {
        using var plugin = await CreateFileMemoryAsync();
        await plugin.StoreAsync("Keep me");
        await plugin.StoreAsync("Edit me");
        await plugin.StoreAsync("Remove me");
        var items = await GetItemsAsync(plugin);

        var result = await plugin.SetItemsAsync(
            "memories",
            [
                Item(" Edited ", items[1].Values["__id"]),
                items[0],
                Item("Brand new", Guid.NewGuid().ToString("D")),
            ]
        );

        Assert.True(result.IsSuccess);
        Assert.Equal(["Edited", "Keep me", "Brand new"], await plugin.GetAllAsync());
        using var reloaded = await CreateFileMemoryAsync();
        Assert.Equal(["Edited", "Keep me", "Brand new"], await reloaded.GetAllAsync());
        Assert.Equal(
            [items[1].Values["__id"], items[0].Values["__id"]],
            (await GetItemsAsync(reloaded)).Take(2).Select(item => item.Values["__id"])
        );
    }

    [Fact]
    public async Task Collection_SaveKeepsAMemoryStoredWhileTheEditorWasOpen()
    {
        using var plugin = await CreateFileMemoryAsync();
        await plugin.StoreAsync("Shown fact");
        var items = await GetItemsAsync(plugin);
        await plugin.StoreAsync("Extracted meanwhile");

        var result = await plugin.SetItemsAsync("memories", [Item("Shown fact, edited", items[0].Values["__id"])]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Shown fact, edited", "Extracted meanwhile"], await plugin.GetAllAsync());
    }

    [Fact]
    public async Task Collection_DeletionInADraftSurvivesAReactivation()
    {
        using var plugin = await CreateFileMemoryAsync();
        await plugin.StoreAsync("Keep");
        await plugin.StoreAsync("Delete");
        var items = await GetItemsAsync(plugin);
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(new TestPluginHostServices(_tempDir));

        var result = await plugin.SetItemsAsync("memories", [items[0]]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Keep"], await plugin.GetAllAsync());
    }

    [Fact]
    public async Task Collection_EmptyLoadWhileDisabledDeletesNothingOnLaterSave()
    {
        using var plugin = await CreateFileMemoryAsync();
        await plugin.StoreAsync("Existing");
        _ = await GetItemsAsync(plugin);
        await plugin.DeactivateAsync();
        Assert.Empty(await GetItemsAsync(plugin));
        await plugin.ActivateAsync(new TestPluginHostServices(_tempDir));

        var result = await plugin.SetItemsAsync("memories", [Item("Added while disabled", null)]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Added while disabled", "Existing"], await plugin.GetAllAsync());
    }

    [Fact]
    public async Task Collection_ConcurrentDuplicateOfAnEditIsNotKeptTwice()
    {
        using var plugin = await CreateFileMemoryAsync();
        await plugin.StoreAsync("Original");
        var items = await GetItemsAsync(plugin);
        await plugin.StoreAsync("Same text");

        await plugin.SetItemsAsync("memories", [Item("Same text", items[0].Values["__id"])]);

        Assert.Equal(["Same text"], await plugin.GetAllAsync());
    }

    [Theory]
    [InlineData("   ", "Memory.Empty")]
    [InlineData("dup", "Memory.Duplicate")]
    public async Task Collection_InvalidSaveIsRejectedAndChangesNothing(string content, string message)
    {
        using var plugin = await CreateFileMemoryAsync();
        await plugin.StoreAsync("dup");
        var before = await File.ReadAllBytesAsync(MemoryPath);
        var items = await GetItemsAsync(plugin);

        var result = await plugin.SetItemsAsync(
            "memories",
            [items[0], Item(content, Guid.NewGuid().ToString("D"))]
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(message, result.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(MemoryPath));
    }

    [Fact]
    public async Task Collection_InactivePluginShowsNothingAndRefusesToSave()
    {
        await File.WriteAllTextAsync(
            MemoryPath,
            "[{\"Content\":\"Kept\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}]"
        );
        using var plugin = new FileMemoryPlugin();

        Assert.Empty(await plugin.GetItemsAsync("memories"));
        var result = await plugin.SetItemsAsync("memories", []);

        Assert.False(result.IsSuccess);
        Assert.Contains("Kept", await File.ReadAllTextAsync(MemoryPath));
        Assert.Equal(
            "Collection.DisabledDescription",
            Assert.Single(plugin.GetCollectionDefinitions()).Description
        );
    }

    [Fact]
    public async Task Action_StoresTrimmedTextOnceAndRejectsEmptyText()
    {
        using var plugin = await CreateFileMemoryAsync();
        var context = new ActionContext(null, null, null, null, "raw");

        var stored = await plugin.ExecuteAsync("  Aurora uses British English \n", context, CancellationToken.None);
        var repeated = await plugin.ExecuteAsync("Aurora uses British English", context, CancellationToken.None);
        var empty = await plugin.ExecuteAsync("  ", context, CancellationToken.None);

        Assert.Equal(new ActionResult(true, "Action.Stored"), stored);
        Assert.Equal(new ActionResult(true, "Action.AlreadyStored"), repeated);
        Assert.Equal(new ActionResult(false, "Action.Empty"), empty);
        Assert.Equal(["Aurora uses British English"], await plugin.GetAllAsync());
    }

    [Fact]
    public async Task Action_CanceledBeforeSaving_StoresNothing()
    {
        using var plugin = await CreateFileMemoryAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.ExecuteAsync("Late fact", new ActionContext(null, null, null, null, "raw"), cts.Token)
        );

        Assert.Empty(await plugin.GetAllAsync(CancellationToken.None));
    }

    private static PluginCollectionItem Item(string content, string? id) =>
        new(new Dictionary<string, string?> { ["content"] = content, ["__id"] = id });

    private static Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(FileMemoryPlugin plugin) =>
        plugin.GetItemsAsync("memories");

    private async Task<FileMemoryPlugin> CreateFileMemoryAsync()
    {
        var plugin = new FileMemoryPlugin();
        plugin.SetLocalization(new TestPluginLocalization());
        await plugin.ActivateAsync(new TestPluginHostServices(_tempDir));
        return plugin;
    }

    private async Task<IMemoryStoragePlugin> CreatePluginAsync()
    {
        var plugin = new FileMemoryPlugin();
        await plugin.ActivateAsync(new TestPluginHostServices(_tempDir));
        return plugin;
    }

    private sealed class TestPluginHostServices(string pluginDataDirectory)
        : IPluginHostServices
    {
        public string PluginDataDirectory { get; } = pluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }

        public IPluginStateStore<T> OpenStateStore<T>(
            string fileName,
            Func<T> createDefault,
            PluginStateStoreOptions? options = null
        )
            where T : notnull =>
            new TestFilePluginStateStore<T>(
                Path.Join(PluginDataDirectory, fileName),
                createDefault,
                options
            );
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

internal sealed class TestFilePluginStateStore<T> : IPluginStateStore<T>
    where T : notnull
{
    private readonly AtomicJsonStore<T> _store;

    public TestFilePluginStateStore(
        string path,
        Func<T> createDefault,
        PluginStateStoreOptions? options
    )
    {
        options ??= new PluginStateStoreOptions();
        _store = new AtomicJsonStore<T>(
            path,
            createDefault,
            new AtomicJsonStoreOptions<T>
            {
                JsonOptions = options.JsonOptions,
                BackupMode = options.KeepLastKnownGoodBackup
                    ? AtomicJsonBackupMode.LastKnownGood
                    : AtomicJsonBackupMode.None,
                CorruptFilePolicy =
                    options.CorruptFilePolicy
                    == PluginStateCorruptFilePolicy.PreserveAndReset
                        ? AtomicJsonCorruptFilePolicy.PreserveAndReset
                        : AtomicJsonCorruptFilePolicy.Throw,
            }
        );
    }

    public ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_store.Current);
    }

    public ValueTask<T> UpdateAsync(
        Func<T, T> update,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_store.Update(update));
    }
}
