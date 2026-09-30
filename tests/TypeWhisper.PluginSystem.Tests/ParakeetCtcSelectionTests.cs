using System.Reflection;
using Moq;
using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ParakeetCtcSelectionTests
{
    [Fact]
    public void SimilaritySelectsTypeWhisperWithoutSwallowingOpen()
    {
        var selected = ParakeetCtcPlugin.SelectReplacements([
            (0, 17, "TypeWhisper", .73, 6.3),
            (5, 12, "TypeWhisper", 1.0, 5.0),
            (10, 7, "TypeWhisper", .64, 4.0),
        ]);

        Assert.Equal(new VocabularyReplacement(5, 12, "TypeWhisper", 5.0), Assert.Single(selected));
    }

    [Fact]
    public void SimilaritySelectsReSharperWithoutSwallowingRun()
    {
        var selected = ParakeetCtcPlugin.SelectReplacements([
            (0, 14, "ReSharper", .69, 6.8),
            (4, 10, "ReSharper", 1.0, 5.1),
            (7, 7, "ReSharper", .78, 5.0),
        ]);

        Assert.Equal(new VocabularyReplacement(4, 10, "ReSharper", 5.1), Assert.Single(selected));
    }

    [Fact]
    public void EqualSimilarityUsesAcousticMargin()
    {
        var selected = ParakeetCtcPlugin.SelectReplacements([
            (0, 14, "ReSharper", .8, 4),
            (4, 10, "ReSharper", .8, 5),
        ]);

        Assert.Equal(new VocabularyReplacement(4, 10, "ReSharper", 5), Assert.Single(selected));
    }

    [Fact]
    public void NonOverlappingProposalsAreKeptInTextOrder()
    {
        var selected = ParakeetCtcPlugin.SelectReplacements([
            (20, 10, "ReSharper", 1, 6),
            (5, 12, "TypeWhisper", .9, 5),
            (17, 3, "CTC", .8, 4),
        ]);

        Assert.Equal([5, 17, 20], selected.Select(p => p.Start));
    }

    [Theory]
    [InlineData("Kestrel hosts", "Kestrel", true)]
    [InlineData("Kestrel hosts", "Kestrels", false)]
    [InlineData("hosts the", "Kestrel", false)]
    [InlineData("re sharper", "ReSharper", false)]
    [InlineData("MyKestrel hosts", "Kestrel", false)]
    [InlineData("(C++) hosts", "C++", true)]
    public void ContainsTermRequiresWholeWord(string original, string term, bool expected)
    {
        Assert.Equal(expected, ParakeetCtcPlugin.WindowAlreadyContainsTerm(original, term));
    }

    [Fact]
    public async Task LifecycleRejectsUnavailableModelAndDisposedActivation()
    {
        var missing = Path.Join(Path.GetTempPath(), "ctc-missing-" + Guid.NewGuid());
        var host = new Mock<IPluginHostServices>(MockBehavior.Strict);
        host.Setup(h => h.GetSetting<string>("ModelDirectory")).Returns(missing);
        var plugin = new ParakeetCtcPlugin();
        var request = new VocabularyRescoreRequest(
            Guid.NewGuid(),
            "private transcript",
            new float[16000],
            16000,
            [new VocabularyTokenTiming("private", 0, .5)],
            [new VocabularyTermHint("TypeWhisper")]
        );

        Assert.False(plugin.IsReady);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.RescoreAsync(request, CancellationToken.None)
        );
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            plugin.ActivateAsync(host.Object)
        );
        Assert.False(plugin.IsReady);
        Assert.False(Directory.Exists(missing));
        host.VerifyGet(h => h.PluginAssetDirectory, Times.Never);
        host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Never);

        plugin.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.ActivateAsync(host.Object));
    }

    [Fact]
    public async Task DisposeWhileActivationHoldsTheGateDoesNotNeedTheCallerContext()
    {
        var assets = Path.Join(Path.GetTempPath(), "ctc-dispose-" + Guid.NewGuid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(
            new BlockingHandler(async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            })
        );
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.GetSetting<string>("ModelDirectory")).Returns((string?)null);
        host.SetupGet(h => h.PluginAssetDirectory).Returns(assets);
        var plugin = new ParakeetCtcPlugin(client);
        try
        {
            var activation = plugin.ActivateAsync(host.Object);
            await entered.Task;
            // A UI thread that blocks in Dispose never pumps its context: any
            // continuation posted there would wait forever.
            var disposed = Task.Factory.StartNew(
                () =>
                {
                    SynchronizationContext.SetSynchronizationContext(new BlockedContext());
                    plugin.Dispose();
                },
                TaskCreationOptions.LongRunning
            );
            Assert.Same(
                disposed,
                await Task.WhenAny(disposed, Task.Delay(TimeSpan.FromSeconds(20)))
            );
            await disposed;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
            Assert.False(plugin.IsReady);
        }
        finally
        {
            if (Directory.Exists(assets))
                Directory.Delete(assets, true);
        }
    }

    private sealed class BlockedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("The context thread is blocked.");
    }

    private sealed class BlockingHandler(Func<CancellationToken, Task> block) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await block(cancellationToken);
            return new HttpResponseMessage();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticsDoNotIncludeTranscriptOrVocabulary(bool invalidTiming)
    {
        const string transcript = "private dictated content";
        const string term = "confidential vocabulary";
        var messages = new List<string>();
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.Log(It.IsAny<PluginLogLevel>(), It.IsAny<string>()))
            .Callback<PluginLogLevel, string>((_, message) => messages.Add(message));
        using var plugin = new ParakeetCtcPlugin();
        // Exercise the real diagnostics before inference, without loading an acoustic model.
        typeof(ParakeetCtcPlugin)
            .GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(plugin, host.Object);
        var request = new VocabularyRescoreRequest(
            Guid.NewGuid(),
            transcript,
            new float[16000],
            16000,
            [new VocabularyTokenTiming(transcript, 0, invalidTiming ? 2 : 1)],
            [new VocabularyTermHint(term, 1)]
        );
        var result = (VocabularyRescoreResult)
            typeof(ParakeetCtcPlugin)
                .GetMethod("Rescore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(plugin, [request, CancellationToken.None])!;

        Assert.Equal(request.RecordingId, result.RecordingId);
        Assert.Empty(result.Replacements);
        Assert.Contains(messages, message => message.Contains("plugin-enter"));
        Assert.Contains(
            messages,
            message => message.Contains(invalidTiming ? "invalid-timing" : "plugin-finish")
        );
        Assert.All(
            messages,
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the assertions are the lambda's whole purpose.
            message =>
            {
                Assert.DoesNotContain(transcript, message);
                Assert.DoesNotContain(term, message);
            }
        );
    }
}
