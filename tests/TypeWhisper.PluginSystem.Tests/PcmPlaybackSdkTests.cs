using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PcmPlaybackSdkTests
{
    [Fact]
    public async Task Old_host_default_member_degrades_to_unavailable_without_throwing()
    {
        IPluginHostServices oldHost = new OldHostServices();

        Assert.False(oldHost.PcmPlayback.IsAvailable);

        var session = await oldHost.PcmPlayback.PlayAsync(
            new PcmPlaybackRequest(
                new byte[2],
                24_000,
                1,
                PcmSampleFormat.Signed16LittleEndian
            ),
            CancellationToken.None
        );

        Assert.False(session.IsActive);
        var completed = 0;
        session.Completed += (_, _) => completed++;
        Assert.Equal(1, completed);
    }

    private sealed class OldHostServices : IPluginHostServices
    {
        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new NoOpLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class NoOpEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            NoOpDisposable.Instance;
    }

    private sealed class NoOpLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
