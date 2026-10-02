using System.Collections.Concurrent;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ParakeetCtcLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Join(
        Path.GetTempPath(),
        "ctc-lifecycle-" + Guid.NewGuid()
    );
    private readonly Mock<IPluginHostServices> _host = new();
    private readonly ConcurrentQueue<string> _messages = new();
    private static readonly IReadOnlyDictionary<string, string> s_metadata = new Dictionary<
        string,
        string
    >
    {
        ["subsampling_factor"] = "8",
        ["normalize_type"] = "per_feature",
    };

    public ParakeetCtcLifecycleTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Join(_directory, "tokens.txt"), "▁ 0\na 1\n<blk> 1024\n");
        File.WriteAllText(
            Path.Join(_directory, "tokenizer.json"),
            """{"model":{"type":"BPE","vocab":{"▁":0,"a":1},"merges":[]}}"""
        );
        File.WriteAllText(
            Path.Join(_directory, "verified-assets.json"),
            JsonSerializer.Serialize(new { })
        );
        _host.Setup(h => h.GetSetting<string>("ModelDirectory")).Returns(_directory);
        _host
            .Setup(h => h.Log(PluginLogLevel.Info, It.IsAny<string>()))
            .Callback<PluginLogLevel, string>((_, message) => _messages.Enqueue(message));
    }

    private static VocabularyRescoreRequest Request() =>
        new(Guid.NewGuid(), "a", ReadOnlyMemory<float>.Empty, 16000, [], []);

    [Fact]
    public async Task ActivationNeedsOnlyTokenizerAndMissingModelWithdrawsReadinessOnFirstRequest()
    {
        using var plugin = new ParakeetCtcPlugin();
        await plugin.ActivateAsync(_host.Object);
        Assert.True(plugin.IsReady);
        _host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Once);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            plugin.RescoreAsync(Request(), CancellationToken.None)
        );
        Assert.False(plugin.IsReady);
        _host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Exactly(2));
        _host.Verify(
            h =>
                h.Log(
                    PluginLogLevel.Error,
                    It.Is<string>(message => message.StartsWith("CTC model failed to load"))
                ),
            Times.Once
        );
        Assert.DoesNotContain(_messages, message => message.StartsWith("CTC model loaded"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.RescoreAsync(Request(), CancellationToken.None)
        );
        _host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Exactly(2));
        // Re-enabling the plugin is the retry path.
        await plugin.ActivateAsync(_host.Object);
        Assert.True(plugin.IsReady);
        await plugin.DeactivateAsync();
        Assert.False(plugin.IsReady);
    }

    [Fact]
    public async Task IdleUnloadKeepsReadyAndNextRequestReloads()
    {
        var loads = 0;
        var disposes = 0;
        using var plugin = new ParakeetCtcPlugin(
            new HttpClient(),
            TimeSpan.FromMilliseconds(100),
            _ =>
            {
                Interlocked.Increment(ref loads);
                return new NemoCtcModel(s_metadata, () => Interlocked.Increment(ref disposes));
            }
        );
        await plugin.ActivateAsync(_host.Object);
        Assert.Equal(0, loads);
        await plugin.RescoreAsync(Request(), CancellationToken.None);
        await plugin.RescoreAsync(Request(), CancellationToken.None);
        Assert.Equal(1, loads);
        await WaitForAsync(() => _messages.Contains("CTC model unloaded after idle"));
        Assert.Equal(1, disposes);
        Assert.True(plugin.IsReady);
        await plugin.RescoreAsync(Request(), CancellationToken.None);
        Assert.Equal(2, loads);
        Assert.Equal(
            2,
            _messages.Count(message =>
                message.StartsWith("CTC model loaded in ") && message.EndsWith(" ms")
            )
        );
        await plugin.DeactivateAsync();
        Assert.Equal(2, disposes);
        Assert.False(plugin.IsReady);
        await Task.Delay(200);
        Assert.Equal(1, _messages.Count(message => message == "CTC model unloaded after idle"));
    }

    [Fact]
    public async Task RequestsResetIdleDeadlineAndDisposeCancelsTimer()
    {
        var disposes = 0;
        using var plugin = new ParakeetCtcPlugin(
            new HttpClient(),
            TimeSpan.FromMilliseconds(500),
            _ => new NemoCtcModel(s_metadata, () => Interlocked.Increment(ref disposes))
        );
        await plugin.ActivateAsync(_host.Object);
        await plugin.RescoreAsync(Request(), CancellationToken.None);
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(150);
            await plugin.RescoreAsync(Request(), CancellationToken.None);
        }
        Assert.Equal(0, disposes);
        // ReSharper disable once DisposeOnUsingVariable -- the explicit Dispose is the behaviour under test; the using covers the exception path.
        plugin.Dispose();
        Assert.Equal(1, disposes);
        await Task.Delay(600);
        Assert.Equal(1, disposes);
        Assert.DoesNotContain("CTC model unloaded after idle", _messages);
    }

    [Fact]
    public async Task CancellationDuringLoadKeepsLoadedModelForNextRequest()
    {
        var loads = 0;
        var disposes = 0;
        using var cancellation = new CancellationTokenSource();
        using var plugin = new ParakeetCtcPlugin(
            new HttpClient(),
            modelFactory: _ =>
            {
                Interlocked.Increment(ref loads);
                // ReSharper disable once AccessToDisposedClosure -- the factory runs inside the awaited RescoreAsync below, before the using scope ends.
                cancellation.Cancel();
                return new NemoCtcModel(s_metadata, () => Interlocked.Increment(ref disposes));
            }
        );
        await plugin.ActivateAsync(_host.Object, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.RescoreAsync(Request(), cancellation.Token)
        );
        Assert.Equal(1, loads);
        Assert.Equal(0, disposes);
        Assert.True(plugin.IsReady);
        await plugin.RescoreAsync(Request(), CancellationToken.None);
        Assert.Equal(1, loads);
        Assert.Equal(0, disposes);
        await plugin.DeactivateAsync();
        Assert.Equal(1, disposes);
    }

    [Fact]
    public async Task MetadataValidationOccursAtLoadAndDisposesRejectedModel()
    {
        var disposes = 0;
        using var plugin = new ParakeetCtcPlugin(
            new HttpClient(),
            modelFactory: _ => new NemoCtcModel(new Dictionary<string, string>(), () => disposes++)
        );
        await plugin.ActivateAsync(_host.Object);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            plugin.RescoreAsync(Request(), CancellationToken.None)
        );
        Assert.Equal(1, disposes);
        Assert.False(plugin.IsReady);
        _host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Exactly(2));
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
