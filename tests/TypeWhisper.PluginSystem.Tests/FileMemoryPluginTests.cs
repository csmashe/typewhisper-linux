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
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
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
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
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
