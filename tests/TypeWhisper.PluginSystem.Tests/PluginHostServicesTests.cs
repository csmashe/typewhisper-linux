// ReSharper disable MethodHasAsyncOverload -- synchronous File.ReadAllBytes is deliberate in these test assertions.
using System.Collections.Immutable;
using Moq;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginHostServicesTests : IDisposable
{
    [Fact]
    public async Task ProcessScope_tracks_completion_and_terminates_remaining_sessions()
    {
        var runner = new RecordingPluginProcessSupervisor();
        var first = new ControlledPluginProcessSession();
        runner.NextSession = first;
        var scope = new PluginProcessSupervisorScope("test-plugin", runner);

        var started = scope.StartSession(
            new ProcessCommand("player", ["one.wav"]),
            new ProcessSessionOptions()
        );

        Assert.True(started.Started);
        Assert.Equal(1, scope.SessionCount);
        first.Complete(new ProcessExitOutcome(ProcessExitReason.Exited, 0));
        await ProcessTestWait.UntilAsync(() => scope.SessionCount == 0);

        var second = new ControlledPluginProcessSession();
        runner.NextSession = second;
        scope.StartSession(
            new ProcessCommand("player", ["two.wav"]),
            new ProcessSessionOptions()
        );

        scope.TerminateAll();

        Assert.Equal(1, second.TerminateCount);
        second.Complete(
            new ProcessExitOutcome(ProcessExitReason.Terminated, null)
        );
        await ProcessTestWait.UntilAsync(() => scope.SessionCount == 0);
    }

    [Fact]
    public async Task ProcessScope_retire_closes_the_scope_while_terminate_leaves_it_usable()
    {
        var runner = new RecordingPluginProcessSupervisor();
        var scope = new PluginProcessSupervisorScope("test-plugin", runner);

        // A failed deactivation leaves the plugin registered, so a plain terminate must
        // stop current work without stranding it on a permanently cancelled scope.
        scope.TerminateAll();

        var afterTerminate = await scope.RunOneShotAsync(
            new ProcessCommand("probe", []),
            new ProcessOneShotOptions()
        );
        Assert.Equal(ProcessRunStatus.Exited, afterTerminate.Status);

        runner.NextSession = new ControlledPluginProcessSession();
        Assert.True(
            scope.StartSession(
                new ProcessCommand("player", ["one.wav"]),
                new ProcessSessionOptions()
            ).Started
        );

        // Retiring is terminal: work crossing the teardown boundary is refused and stopped.
        scope.Retire();

        var startsBeforeRefusal = runner.Sessions.Count;
        var late = new ControlledPluginProcessSession();
        runner.NextSession = late;
        var refused = scope.StartSession(
            new ProcessCommand("player", ["two.wav"]),
            new ProcessSessionOptions()
        );

        Assert.False(refused.Started);
        // Refused before the handoff, so the command never ran at all.
        Assert.Equal(startsBeforeRefusal, runner.Sessions.Count);
        Assert.Equal(0, late.TerminateCount);
        Assert.False(scope.LaunchDetached(new ProcessCommand("tool", [])).Started);
        Assert.False(scope.LaunchUri(new Uri("https://example.test/")).Started);
        Assert.Empty(runner.Uris);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scope.RunOneShotAsync(
                new ProcessCommand("probe", []),
                new ProcessOneShotOptions()
            )
        );

        // A terminate arriving after retirement must not hand the scope back its lifetime.
        scope.TerminateAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scope.RunOneShotAsync(
                new ProcessCommand("probe", []),
                new ProcessOneShotOptions()
            )
        );
    }

    [Fact]
    public void ProcessScope_terminates_a_session_that_crosses_a_stop_sweep()
    {
        var runner = new RecordingPluginProcessSupervisor();
        var scope = new PluginProcessSupervisorScope("test-plugin", runner);
        var crossing = new ControlledPluginProcessSession();

        // The sweep runs while the launch is in flight, so this session was never in the
        // snapshot TerminateAll took and must be stopped as it tries to register.
        runner.OnStartSession = scope.TerminateAll;
        runner.NextSession = crossing;

        var started = scope.StartSession(
            new ProcessCommand("player", ["one.wav"]),
            new ProcessSessionOptions()
        );

        Assert.False(started.Started);
        Assert.Equal(1, crossing.TerminateCount);
        Assert.Equal(0, scope.SessionCount);
    }

    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IPluginEventBus> _eventBus = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly string _tempDir;

    public PluginHostServicesTests()
    {
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
        _tempDir = TestPaths.CreateTempDirectory(
            "TypeWhisper.PluginHostServicesTests"
        );
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
    public void NotifyCapabilitiesChanged_InvokesCallback()
    {
        var callbackInvoked = false;
        var services = CreateServices(() => callbackInvoked = true);

        services.NotifyCapabilitiesChanged();

        Assert.True(callbackInvoked);
    }

    [Fact]
    public void NotifyCapabilitiesChanged_WithNoCallback_DoesNotThrow()
    {
        var services = CreateServices();
        var ex = Record.Exception(services.NotifyCapabilitiesChanged);
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyCapabilitiesChanged_CallbackInvokedMultipleTimes()
    {
        var callCount = 0;
        var services = CreateServices(() => callCount++);

        services.NotifyCapabilitiesChanged();
        services.NotifyCapabilitiesChanged();
        services.NotifyCapabilitiesChanged();

        Assert.Equal(3, callCount);
    }

    [Fact]
    public void Constructor_WithoutCallback_DoesNotThrow()
    {
        var ex = Record.Exception(() => CreateServices());
        Assert.Null(ex);
    }

    [Fact]
    public void Localization_IsAvailable()
    {
        var services = CreateServices();
        Assert.NotNull(services.Localization);
    }

    [Fact]
    public void Localization_ReturnsKeyWhenNoFiles()
    {
        var services = CreateServices();
        Assert.Equal("some.key", services.Localization.GetString("some.key"));
    }

    [Fact]
    public void Localization_AvailableLanguagesEmpty_WhenNoFiles()
    {
        var services = CreateServices();
        Assert.Empty(services.Localization.AvailableLanguages);
    }

    [Fact]
    public void PluginDataDirectory_UsesInjectedRoot()
    {
        var services = CreateServices();

        var directory = services.PluginDataDirectory;

        Assert.Equal(Path.Join(_tempDir, "PluginData", "test-plugin"), directory);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void PluginAssetDirectory_WithSettingsAndNoCustomPath_UsesInjectedRoot()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Current).Returns(new AppSettings());
        var services = CreateServices(settings: settings.Object);

        Assert.Equal(services.PluginDataDirectory, services.PluginAssetDirectory);
        Assert.StartsWith(Path.Join(_tempDir, "PluginData"), services.PluginAssetDirectory);
    }

    [Fact]
    public async Task SettingsAndSecrets_RoundTripAcrossServiceInstances()
    {
        var writer = CreateServices();
        writer.SetSetting("language", "en-US");
        await writer.StoreSecretAsync("api-key", "secret-value");

        var reader = CreateServices();

        Assert.Equal("en-US", reader.GetSetting<string>("language"));
        Assert.Equal("secret-value", await reader.LoadSecretAsync("api-key"));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task StoreSecret_WritesV2EnvelopeUsingOwnerOnlyKeyFile()
    {
        var services = CreateServices();

        await services.StoreSecretAsync("api-key", "secret-value");

        var settingsPath = Path.Join(
            _tempDir,
            "PluginData",
            "test-plugin",
            "settings.json"
        );
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var stored = document.RootElement
            .GetProperty("secret:api-key")
            .GetString()!;
        var envelope = Convert.FromBase64String(stored);
        var keyPath = Path.Join(_tempDir, "secret-protection.key");

        Assert.Equal("TWSP"u8.ToArray(), envelope[..4]);
        Assert.Equal(2, envelope[4]);
        Assert.Equal(32, File.ReadAllBytes(keyPath).Length);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(keyPath)
        );
    }

    [Fact]
    public async Task LoadSecret_LegacyGcmMigratesWholeFileToV2()
    {
        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        Directory.CreateDirectory(pluginDirectory);
        var firstLegacy = EncryptLegacyGcm("first-secret");
        var secondLegacy = EncryptLegacyGcm("second-secret");
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["secret:first"] = firstLegacy,
                    ["secret:second"] = secondLegacy,
                }
            )
        );

        var loaded = await CreateServices().LoadSecretAsync("first");

        Assert.Equal("first-secret", loaded);
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var stored = property.Value.GetString()!;
            Assert.NotEqual(
                property.Name == "secret:first" ? firstLegacy : secondLegacy,
                stored
            );
            var envelope = Convert.FromBase64String(stored);
            Assert.Equal("TWSP"u8.ToArray(), envelope[..4]);
            Assert.Equal(2, envelope[4]);
        }

        Assert.Equal(
            "second-secret",
            await CreateServices().LoadSecretAsync("second")
        );
    }

    [Fact]
    public async Task LoadSecret_UndecryptableValueReturnsNullAndPreservesCiphertext()
    {
        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        Directory.CreateDirectory(pluginDirectory);
        var tampered = Convert.FromBase64String(EncryptLegacyGcm("secret-value"));
        tampered[^1] ^= 0x04;
        var stored = Convert.ToBase64String(tampered);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(
                new Dictionary<string, string> { ["secret:api-key"] = stored }
            )
        );
        var before = File.ReadAllBytes(settingsPath);

        var loaded = await CreateServices().LoadSecretAsync("api-key");

        Assert.Null(loaded);
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.Equal(
            stored,
            document.RootElement.GetProperty("secret:api-key").GetString()
        );
    }

    [Fact]
    public async Task LoadSecret_AnotherUndecryptableSecretPreventsPartialLazyMigration()
    {
        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        Directory.CreateDirectory(pluginDirectory);
        var valid = EncryptLegacyGcm("valid-secret");
        var tampered = Convert.FromBase64String(EncryptLegacyGcm("invalid-secret"));
        tampered[^1] ^= 0x02;
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["secret:valid"] = valid,
                    ["secret:invalid"] = Convert.ToBase64String(tampered),
                }
            )
        );
        var before = File.ReadAllBytes(settingsPath);

        var loaded = await CreateServices().LoadSecretAsync("valid");

        Assert.Null(loaded);
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
    }

    [Fact]
    public async Task FailedSave_ThrowsWithoutChangingCacheOrSettingsFile()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            // Root can bypass directory write permissions, so chmod cannot force this failure.
            return;
        }

        var services = CreateServices();
        services.SetSetting("language", "old-value");
        await services.StoreSecretAsync("api-key", "old-secret");

        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        var originalMode = File.GetUnixFileMode(pluginDirectory);
        var originalBytes = File.ReadAllBytes(settingsPath);

        try
        {
            File.SetUnixFileMode(
                pluginDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
            );

            Assert.ThrowsAny<Exception>(() =>
                services.SetSetting("language", "new-value")
            );
            await Assert.ThrowsAnyAsync<Exception>(() =>
                services.StoreSecretAsync("api-key", "new-secret")
            );

            Assert.Equal("old-value", services.GetSetting<string>("language"));
            Assert.Equal("old-secret", await services.LoadSecretAsync("api-key"));
            Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
            Assert.Empty(Directory.EnumerateFiles(pluginDirectory, "*.tmp"));
        }
        finally
        {
            File.SetUnixFileMode(pluginDirectory, originalMode);
        }
    }

    [Fact]
    public void CorruptSettingsFile_IsPreservedBeforeFreshSettingsAreSaved()
    {
        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        Directory.CreateDirectory(pluginDirectory);
        var originalBytes = "{ not valid json"u8.ToArray();
        File.WriteAllBytes(settingsPath, originalBytes);

        var services = CreateServices();

        Assert.Null(services.GetSetting<string>("language"));
        var brokenPath = Assert.Single(
            Directory.EnumerateFiles(pluginDirectory, "settings.json.broken-*")
        );
        Assert.Equal(originalBytes, File.ReadAllBytes(brokenPath));

        services.SetSetting("language", "en-US");

        Assert.True(File.Exists(settingsPath));
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.Equal("en-US", document.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public void UnreadableSettingsFile_RefusesToOverwriteExistingFile()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            // Root can read a mode-000 file, so this cannot exercise the unreadable-file path.
            return;
        }

        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        Directory.CreateDirectory(pluginDirectory);
        var originalBytes = "{\"language\":\"old-value\"}"u8.ToArray();
        File.WriteAllBytes(settingsPath, originalBytes);
        var originalMode = File.GetUnixFileMode(settingsPath);

        try
        {
            File.SetUnixFileMode(settingsPath, UnixFileMode.None);
            var services = CreateServices();

            Assert.ThrowsAny<Exception>(() =>
                services.GetSetting<string>("language")
            );
            Assert.ThrowsAny<Exception>(() =>
                services.SetSetting("language", "new-value")
            );
            Assert.True(File.Exists(settingsPath));
        }
        finally
        {
            File.SetUnixFileMode(settingsPath, originalMode);
        }

        Assert.Equal(originalMode, File.GetUnixFileMode(settingsPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
    }

    [Fact]
    public async Task DeleteSecret_RemovesPersistedSecret()
    {
        var services = CreateServices();
        await services.StoreSecretAsync("api-key", "secret-value");

        await services.DeleteSecretAsync("api-key");

        Assert.Null(await services.LoadSecretAsync("api-key"));
        Assert.Null(await CreateServices().LoadSecretAsync("api-key"));
    }

    [Fact]
    public async Task DeleteSecret_ThrowsWhenFileUnreadableAndLeavesSecretOnDisk()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            // Root can read a mode-000 file, so this cannot exercise the unreadable-file path.
            return;
        }

        var writer = CreateServices();
        await writer.StoreSecretAsync("api-key", "secret-value");

        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        var originalBytes = File.ReadAllBytes(settingsPath);
        var originalMode = File.GetUnixFileMode(settingsPath);

        try
        {
            File.SetUnixFileMode(settingsPath, UnixFileMode.None);
            var services = CreateServices();

            await Assert.ThrowsAnyAsync<Exception>(() =>
                services.DeleteSecretAsync("api-key")
            );
        }
        finally
        {
            File.SetUnixFileMode(settingsPath, originalMode);
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
        Assert.Equal("secret-value", await CreateServices().LoadSecretAsync("api-key"));
    }

    [Fact]
    public async Task GetSetting_ReservedSecretKeyThrowsAndNeverReturnsCiphertext()
    {
        var services = CreateServices();
        await services.StoreSecretAsync("api-key", "secret-value");

        var ex = Assert.Throws<ArgumentException>(
            () => services.GetSetting<string>("secret:api-key")
        );

        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
        Assert.Equal("secret-value", await services.LoadSecretAsync("api-key"));
    }

    [Fact]
    public async Task SetSetting_ReservedSecretKeyThrowsAndLeavesStoredSecretIntact()
    {
        var services = CreateServices();
        await services.StoreSecretAsync("api-key", "secret-value");
        var settingsPath = Path.Join(
            _tempDir,
            "PluginData",
            "test-plugin",
            "settings.json"
        );
        var before = File.ReadAllBytes(settingsPath);

        var ex = Assert.Throws<ArgumentException>(
            () => services.SetSetting("secret:api-key", "plaintext-override")
        );

        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
        Assert.Equal("secret-value", await CreateServices().LoadSecretAsync("api-key"));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("sub/state.json")]
    [InlineData("sub\\state.json")]
    [InlineData("settings.json")]
    [InlineData("secret-protection.key")]
    public void OpenStateStore_RejectsNonLeafAndReservedNames(string fileName)
    {
        var services = CreateServices();

        Assert.Throws<ArgumentException>(() =>
            services.OpenStateStore<ImmutableArray<string>>(
                fileName,
                static () => []
            )
        );
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void OpenStateStore_RejectsRelativeDirectoryNames(string fileName)
    {
        var services = CreateServices();

        Assert.Throws<ArgumentException>(() =>
            services.OpenStateStore<ImmutableArray<string>>(
                fileName,
                static () => []
            )
        );
    }

    [Fact]
    public void OpenStateStore_ReusesSamePathTypeAndOptionsButRejectsConflicts()
    {
        var services = CreateServices();
        var options = new PluginStateStoreOptions
        {
            JsonOptions = new JsonSerializerOptions { WriteIndented = true },
            CorruptFilePolicy = PluginStateCorruptFilePolicy.PreserveAndReset,
        };

        var first = services.OpenStateStore<ImmutableArray<string>>(
            "state.json",
            static () => [],
            options
        );
        var second = services.OpenStateStore<ImmutableArray<string>>(
            "state.json",
            static () => [],
            options
        );

        Assert.Same(first, second);
        Assert.Throws<InvalidOperationException>(() =>
            services.OpenStateStore<ImmutableArray<int>>(
                "state.json",
                static () => [],
                options
            )
        );
        Assert.Throws<InvalidOperationException>(() =>
            services.OpenStateStore<ImmutableArray<string>>(
                "state.json",
                static () => [],
                options with { KeepLastKnownGoodBackup = true }
            )
        );
    }

    [Fact]
    public async Task OpenStateStore_CancellationBeforeCommit_DoesNotWrite()
    {
        var services = CreateServices();
        var store = services.OpenStateStore<ImmutableArray<string>>(
            "state.json",
            static () => []
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
                await store.UpdateAsync(
                    current => current.Add("not committed"),
                    cancellation.Token
                )
        );

        Assert.False(
            File.Exists(
                Path.Join(
                    _tempDir,
                    "PluginData",
                    "test-plugin",
                    "state.json"
                )
            )
        );
    }

    private PluginHostServices CreateServices(
        Action? onCapabilitiesChanged = null,
        ISettingsService? settings = null
    )
    {
        return new PluginHostServices(
            "test-plugin",
            _tempDir,
            _activeWindow.Object,
            _eventBus.Object,
            _profiles.Object,
            settings,
            onCapabilitiesChanged,
            pluginDataRoot: Path.Join(_tempDir, "PluginData")
        );
    }

    private static string EncryptLegacyGcm(string plainText)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{Environment.UserName}:{Environment.GetEnvironmentVariable("HOME") ?? "/"}"
        );
        var key = Rfc2898DeriveBytes.Pbkdf2(
            material,
            "TypeWhisper.ApiKey.v1.linux"u8.ToArray(),
            10_000,
            HashAlgorithmName.SHA256,
            32
        );
        var plaintext = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        var combined = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        combined[0] = 1;
        Buffer.BlockCopy(nonce, 0, combined, 1, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, 1 + nonce.Length, tag.Length);
        Buffer.BlockCopy(
            cipher,
            0,
            combined,
            1 + nonce.Length + tag.Length,
            cipher.Length
        );
        return Convert.ToBase64String(combined);
    }
}

internal sealed class RecordingPluginProcessSupervisor : IProcessRunner
{
    public ControlledPluginProcessSession? NextSession { get; set; }
    public List<(ProcessCommand Command, ProcessSessionOptions Options)> Sessions { get; } = [];
    public List<Uri> Uris { get; } = [];
    // ReSharper disable once MemberCanBePrivate.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global -- settable knob on the fake, mirroring NextSession; narrowing it would make it unsettable
    public ProcessRunOutcome OneShotOutcome { get; set; } = new(
        ProcessRunStatus.Exited,
        0,
        [],
        [],
        ProcessOutputStatus.Complete,
        null
    );

    // Honours the token like the real runner, so scope-lifetime cancellation is observable.
    public ProcessRunOutcome RunProbe(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OneShotOutcome;
    }

    public Task<ProcessRunOutcome> RunOneShotAsync(
        ProcessCommand command,
        ProcessOneShotOptions options,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OneShotOutcome);
    }

    /// <summary>Fires mid-launch so tests can interleave a scope stop with registration.</summary>
    public Action? OnStartSession { get; set; }

    public ProcessSessionStartOutcome StartSession(
        ProcessCommand command,
        ProcessSessionOptions options
    )
    {
        Sessions.Add((command, options));
        OnStartSession?.Invoke();
        return NextSession is { } session
            ? new ProcessSessionStartOutcome(session, null)
            : new ProcessSessionStartOutcome(null, "fake start failure");
    }

    public DetachedLaunchOutcome LaunchDetached(ProcessCommand command) =>
        new(true, null);

    public DetachedLaunchOutcome LaunchUri(Uri uri)
    {
        Uris.Add(uri);
        return new DetachedLaunchOutcome(true, null);
    }

    public Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        bool detachAfterExit = false,
        CancellationToken ct = default
    ) => throw new NotSupportedException();
}

internal sealed class ControlledPluginProcessSession : IPluginProcessSession
{
    private readonly TaskCompletionSource<ProcessExitOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int TerminateCount { get; private set; }
    public int ProcessId => 1234;
    public bool IsRunning => !_completion.Task.IsCompleted;
    public Task<ProcessExitOutcome> Completion => _completion.Task;

    public async IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default
    )
    {
        await Completion.WaitAsync(cancellationToken);
        yield break;
    }

    public ValueTask WriteStandardInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public ValueTask CompleteStandardInputAsync(
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public void Terminate()
    {
        TerminateCount++;
    }

    public void Dispose()
    {
        Terminate();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Complete(ProcessExitOutcome outcome)
    {
        _completion.TrySetResult(outcome);
    }
}

internal static class ProcessTestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met before the deadline.");
    }
}
