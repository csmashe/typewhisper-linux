using System.Collections.Concurrent;
using System.Text;
using TypeWhisper.Plugin.Obsidian;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ObsidianPluginTests : IDisposable
{
    private const string NotesSubfolder = "Test Notes";

    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.ObsidianPluginTests"
    );

    private string VaultDirectory => Path.Join(_tempDir, "vault");
    private string PluginDataDirectory => Path.Join(_tempDir, "plugin-data");
    private string NotesDirectory => Path.Join(VaultDirectory, NotesSubfolder);

    public ObsidianPluginTests()
    {
        Directory.CreateDirectory(VaultDirectory);
        Directory.CreateDirectory(PluginDataDirectory);
    }

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
    public async Task ConcurrentIndividualSaves_WithFixedStem_PreserveEveryInputInDistinctFiles()
    {
        const int saveCount = 32;
        var (sut, _) = await CreatePluginAsync(dailyNoteMode: false);
        var inputs = Enumerable.Range(0, saveCount)
            .Select(index => $"individual-input-{index:D3}-{Guid.NewGuid():N}")
            .ToArray();

        var results = await RunConcurrentlyAsync(
            inputs,
            input => sut.ExecuteAsync(input, EmptyContext(), CancellationToken.None)
        );

        Assert.All(results, result => Assert.True(result.Success));
        var notePaths = Directory.GetFiles(NotesDirectory, "*.md");
        Assert.Equal(saveCount, notePaths.Length);

        var noteLines = await ReadAllLinesAsync(notePaths);
        Assert.All(inputs, input => Assert.Equal(1, noteLines.Count(line => line == input)));
    }

    [Fact]
    public async Task ConcurrentDailyAppends_WriteOneHeaderAndEveryEntryExactlyOnce()
    {
        const int saveCount = 32;
        var (sut, _) = await CreatePluginAsync(dailyNoteMode: true);
        var inputs = Enumerable.Range(0, saveCount)
            .Select(index => $"daily-input-{index:D3}-{Guid.NewGuid():N}")
            .ToArray();

        var results = await RunConcurrentlyAsync(
            inputs,
            input => sut.ExecuteAsync(input, EmptyContext(), CancellationToken.None)
        );

        Assert.All(results, result => Assert.True(result.Success));
        var notePath = Assert.Single(Directory.GetFiles(NotesDirectory, "*.md"));
        var lines = await File.ReadAllLinesAsync(notePath);
        Assert.Equal(1, lines.Count(line => line.StartsWith("# ", StringComparison.Ordinal)));
        Assert.All(inputs, input => Assert.Equal(1, lines.Count(line => line == input)));
    }

    [Fact]
    public async Task DailyAppend_CanceledWhileWaitingForLock_ThrowsAndLeavesNoteUntouched()
    {
        var (sut, _) = await CreatePluginAsync(dailyNoteMode: true);
        Directory.CreateDirectory(NotesDirectory);
        var notePath = Path.Join(NotesDirectory, $"{DateTime.Now:yyyy-MM-dd}.md");
        const string originalContent = "# Existing daily note\n\nOriginal entry\n";
        await File.WriteAllTextAsync(notePath, originalContent, Encoding.UTF8);

        var lockPath = ObsidianPlugin.GetDailyNoteLockPath(PluginDataDirectory, notePath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.ExecuteAsync("must-not-be-written", EmptyContext(), cancellation.Token)
        );

        // ReSharper disable once MethodSupportsCancellation -- the only token in scope is the already-canceled cancellation.Token; passing it would abort this verification read that must succeed.
        Assert.Equal(originalContent, await File.ReadAllTextAsync(notePath, Encoding.UTF8));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(VaultDirectory, "*", SearchOption.AllDirectories),
            path => Path.GetExtension(path).Equals(".lock", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task IndividualSave_FailedNewlyOwnedWrite_DeletesPartialFile()
    {
        Directory.CreateDirectory(NotesDirectory);
        var notePath = Path.Join(NotesDirectory, "Failed Note.md");

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            ObsidianPlugin.WriteIndividualNoteAsync(
                notePath,
                "complete content",
                CancellationToken.None,
                async (stream, _, ct) =>
                {
                    await stream.WriteAsync("partial"u8.ToArray(), ct);
                    throw new IOException("Injected write failure.");
                }
            )
        );

        Assert.Equal("Injected write failure.", exception.Message);
        Assert.False(File.Exists(notePath));
        Assert.Empty(Directory.EnumerateFiles(NotesDirectory));
    }

    private async Task<(ObsidianPlugin Plugin, TestPluginHostServices Host)> CreatePluginAsync(
        bool dailyNoteMode
    )
    {
        var host = new TestPluginHostServices(PluginDataDirectory);
        host.SetSetting("vault-path", VaultDirectory);
        host.SetSetting("subfolder", NotesSubfolder);
        host.SetSetting("daily-note-mode", dailyNoteMode);
        host.SetSetting("filename-template", "Fixed Individual Note");

        var plugin = new ObsidianPlugin(() => []);
        await plugin.ActivateAsync(host);
        return (plugin, host);
    }

    private static ActionContext EmptyContext() => new(null, null, null, null, null);

    private static async Task<ActionResult[]> RunConcurrentlyAsync(
        string[] inputs,
        Func<string, Task<ActionResult>> saveAsync
    )
    {
        using var ready = new CountdownEvent(inputs.Length);
        using var start = new ManualResetEventSlim(initialState: false);

        var tasks = inputs.Select(input =>
                Task.Factory.StartNew(
                    async () =>
                    {
                        // ReSharper disable AccessToDisposedClosure -- Task.WhenAll below awaits every task before the `using var ready`/`start` are disposed at scope end.
                        ready.Signal();
                        start.Wait();
                        // ReSharper restore AccessToDisposedClosure
                        return await saveAsync(input);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                ).Unwrap()
            )
            .ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        start.Set();
        return await Task.WhenAll(tasks);
    }

    private static async Task<List<string>> ReadAllLinesAsync(IEnumerable<string> paths)
    {
        var lines = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            paths,
            async (path, ct) =>
            {
                foreach (var line in await File.ReadAllLinesAsync(path, ct))
                    lines.Add(line);
            }
        );
        return lines.ToList();
    }

    private sealed class TestPluginHostServices(string pluginDataDirectory)
        : IPluginHostServices
    {
        private readonly Dictionary<string, object?> _settings = [];

        public string PluginDataDirectory { get; } = pluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? (T?)value : default;

        public void SetSetting<T>(string key, T value) => _settings[key] = value;
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => key;
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
