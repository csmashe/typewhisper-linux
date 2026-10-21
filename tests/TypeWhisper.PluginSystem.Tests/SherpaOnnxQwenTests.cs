extern alias SherpaOnnx;

using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Moq;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using SherpaCuda = SherpaOnnx::TypeWhisper.Plugins.Shared.Cuda;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxQwenTests : IDisposable
{
    private const int SampleRate = SherpaDecodeCoordinator.SampleRate;
    private const string BaseUrl = "https://example.test/repo";

    private static readonly byte[] s_onnx = [0x08, 0x09, 0x3a, 0x02, 0x12, 0x00];

    // Smallest Qwen layout the structural preflight accepts.
    private static readonly Dictionary<string, byte[]> s_qwenContent = new()
    {
        ["conv_frontend.onnx"] = s_onnx,
        ["encoder.int8.onnx"] = s_onnx,
        ["decoder.int8.onnx"] = s_onnx,
        ["tokenizer/merges.txt"] = "#version: 0.2\nĠ t\nĠ a\n"u8.ToArray(),
        ["tokenizer/vocab.json"] = """{"a": 0, "b": 1}"""u8.ToArray(),
        ["tokenizer/tokenizer_config.json"] = """{"model_max_length": 131072}"""u8.ToArray(),
    };

    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "tw-sherpa-qwen-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Catalog_PinsBothQwenModelsToOneSharedTokenizer()
    {
        var catalog = SherpaOnnxPlugin.DefaultModelsForTests;
        var small = catalog.Single(m => m.Id == SherpaOnnxPlugin.Qwen06BModelId);
        var large = catalog.Single(m => m.Id == SherpaOnnxPlugin.Qwen17BModelId);

        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- asserting on each model is the test's purpose.
        Assert.All([small, large], model =>
        {
            Assert.Equal(SherpaOnnxPlugin.SherpaModelFamily.Qwen3Asr, model.Family);
            Assert.Equal(s_qwenContent.Keys, model.Files.Select(f => f.FileName));
            Assert.False(model.SupportsTranslation);
            Assert.False(model.IsRecommended);
            Assert.Equal(30, model.LanguageCount);
        });
        Assert.Equal(
            small.Files.Where(f => f.FileName.StartsWith("tokenizer/", StringComparison.Ordinal)),
            large.Files.Where(f => f.FileName.StartsWith("tokenizer/", StringComparison.Ordinal)),
            (a, b) => a.Sha256 == b.Sha256 && a.SizeBytes == b.SizeBytes
        );
        Assert.Equal(987, small.EstimatedSizeMB);
        Assert.Equal(small.Files.Sum(f => f.SizeBytes) / 1_000_000, small.EstimatedSizeMB);
        Assert.Equal(large.Files.Sum(f => f.SizeBytes) / 1_000_000, large.EstimatedSizeMB);
    }

    [Fact]
    public void Preflight_AcceptsTheQwenLayoutAndRejectsADamagedTokenizer()
    {
        var dir = WriteQwenFiles(Path.Join(_root, "Models", SherpaOnnxPlugin.Qwen06BModelId));
        SherpaOnnxPlugin.RunArtifactPreflightForTests(SherpaOnnxPlugin.Qwen06BModelId, dir);

        var vocab = Path.Join(dir, "tokenizer", "vocab.json");
        File.WriteAllText(vocab, """{"a": 0, "b":""");
        Assert.Throws<InvalidDataException>(() =>
            SherpaOnnxPlugin.RunArtifactPreflightForTests(SherpaOnnxPlugin.Qwen06BModelId, dir));

        File.WriteAllText(vocab, "[]");
        Assert.Throws<InvalidDataException>(() =>
            SherpaOnnxPlugin.RunArtifactPreflightForTests(SherpaOnnxPlugin.Qwen06BModelId, dir));

        File.WriteAllBytes(vocab, s_qwenContent["tokenizer/vocab.json"]);
        var merges = Path.Join(dir, "tokenizer", "merges.txt");
        File.WriteAllText(merges, "#version: 0.2\n");
        Assert.Throws<InvalidDataException>(() =>
            SherpaOnnxPlugin.RunArtifactPreflightForTests(SherpaOnnxPlugin.Qwen06BModelId, dir));

        File.WriteAllText(merges, "#version: 0.2\nĠ t extra\n");
        Assert.Throws<InvalidDataException>(() =>
            SherpaOnnxPlugin.RunArtifactPreflightForTests(SherpaOnnxPlugin.Qwen06BModelId, dir));

        File.WriteAllBytes(merges, [0xC3, 0x28, 0x20, 0x61]);
        Assert.Throws<InvalidDataException>(() =>
            SherpaOnnxPlugin.RunArtifactPreflightForTests(SherpaOnnxPlugin.Qwen06BModelId, dir));
    }

    [Fact]
    public async Task Download_PublishesTokenizerFilesInTheirSubdirectory()
    {
        var server = new FakeRepository();
        using var plugin = CreateDownloader(server, "qwen-test");
        var dir = Path.Join(_root, "Models", "qwen-test");
        Directory.CreateDirectory(Path.Join(dir, "tokenizer"));
        var abandoned = Path.Join(dir, "tokenizer", $"vocab.json.{Guid.NewGuid():N}.partial");
        await File.WriteAllBytesAsync(abandoned, [1, 2, 3]);

        await plugin.DownloadModelAsync("qwen-test", null, CancellationToken.None);

        Assert.True(plugin.IsModelDownloaded("qwen-test"));
        foreach (var (name, bytes) in s_qwenContent)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Join(dir, name)));
        Assert.Contains("/repo/tokenizer/vocab.json", server.RequestedPaths);
        Assert.False(File.Exists(abandoned));
        Assert.Empty(Directory.GetFiles(dir, "*.partial", SearchOption.AllDirectories));

        await plugin.DeleteModelAsync("qwen-test", CancellationToken.None);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void SelectingQwen_OffersItsLanguagesWithAutomaticDetectionAndNoTranslation()
    {
        using var plugin = new SherpaOnnxPlugin();

        foreach (var id in new[] { SherpaOnnxPlugin.Qwen06BModelId, SherpaOnnxPlugin.Qwen17BModelId })
        {
            plugin.SelectModel(id);
            Assert.Equal(30, plugin.SupportedLanguages.Count);
            Assert.Contains("en", plugin.SupportedLanguages);
            Assert.Contains("yue", plugin.SupportedLanguages);
            // The 1.7B export misreads some windows when it detects the language itself.
            Assert.Equal(
                id == SherpaOnnxPlugin.Qwen06BModelId ? LanguageSelectionSupport.Supported : LanguageSelectionSupport.Unsupported,
                plugin.AutomaticDetectionSupport);
            Assert.Equal(LanguageSelectionSupport.Supported, plugin.ExplicitSelectionSupport);
            Assert.False(plugin.SupportsTranslation);
            Assert.False(plugin.TranscriptionModels.Single(m => m.Id == id).SupportsTranslation);
        }

        // The other models keep what they declared before.
        plugin.SelectModel("parakeet-ultra-0.6b");
        Assert.Empty(plugin.SupportedLanguages);
        Assert.Equal(LanguageSelectionSupport.Unsupported, plugin.ExplicitSelectionSupport);
        Assert.Null(plugin.TranscriptionModels.Single(m => m.Id == "parakeet-ultra-0.6b").SupportsTranslation);
        plugin.SelectModel("canary-180m-flash");
        Assert.Equal(["en", "de", "fr", "es"], plugin.SupportedLanguages);
        Assert.True(plugin.SupportsTranslation);
    }

    [Fact]
    public async Task Load_CreatesTheQwenRecognizerOnTheCpuWhenCudaIsChosen()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var plugin = new SherpaOnnxPlugin();
        plugin.SetCudaDependenciesForTests(new ReadyProvisioner(_root), new NoopInstaller(_root));
        plugin.SetHostForTests(CreateHost().Object);
        // A CPU-only runtime is already loaded, so the CUDA one is never wired into the test process.
        plugin.MarkNativeRuntimeLoadedForTests("cpu", effectiveProvider: "cuda");
        string? provider = null;
        plugin.SetQwenRecognizerFactoryForTests((_, requested) =>
        {
            provider = requested;
            throw new InvalidOperationException("stop before the native runtime");
        });
        WriteQwenFiles(Path.Join(_root, "Models", SherpaOnnxPlugin.Qwen06BModelId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.LoadModelAsync(SherpaOnnxPlugin.Qwen06BModelId, CancellationToken.None));

        Assert.Equal("cpu", provider);
        // The other models still get CUDA.
        Assert.Equal("cuda", plugin.ComputeBackendForTests);
    }

    [Theory]
    [InlineData(SherpaOnnxPlugin.Qwen06BModelId, "cuda", TranscriptionAccelerationBackend.Cpu, false)]
    [InlineData("parakeet-ultra-0.6b", "cuda", TranscriptionAccelerationBackend.NvidiaCuda, false)]
    // A CPU-only runtime still needs the restart before the other models can use CUDA.
    [InlineData(SherpaOnnxPlugin.Qwen06BModelId, "cpu", TranscriptionAccelerationBackend.Cpu, true)]
    public void ReapplyingCuda_KeepsALoadedQwenRecognizerReportedOnTheCpu(
        string modelId,
        string wiredRuntime,
        TranscriptionAccelerationBackend expected,
        bool requiresRestart)
    {
        using var plugin = new SherpaOnnxPlugin();
        // Stands in for a model loaded while CUDA was chosen.
        plugin.MarkNativeRuntimeLoadedForTests(wiredRuntime, loadedModelId: modelId);

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(modelId, plugin.LoadedModelIdForTests);
        Assert.Equal(expected, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal(requiresRestart, plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public async Task Load_RemovesADamagedQwenModelWithItsTokenizerDirectory()
    {
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(CreateHost().Object);
        var dir = WriteQwenFiles(Path.Join(_root, "Models", SherpaOnnxPlugin.Qwen06BModelId));
        await File.WriteAllTextAsync(Path.Join(dir, "tokenizer", "vocab.json"), """{"a": 0,""");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.LoadModelAsync(SherpaOnnxPlugin.Qwen06BModelId, CancellationToken.None));

        Assert.False(plugin.IsModelDownloaded(SherpaOnnxPlugin.Qwen06BModelId));
        Assert.False(Directory.Exists(Path.Join(dir, "tokenizer")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("en", "English")]
    [InlineData(" EN ", "English")]
    [InlineData("yue", "Cantonese")]
    [InlineData("fil", "Filipino")]
    public void QwenLanguageName_MapsSupportedCodesToTheModelsNames(string? code, string? expected)
    {
        Assert.Equal(expected, SherpaOnnxPlugin.QwenLanguageName(code));
    }

    [Fact]
    public void QwenLanguageName_RejectsALanguageTheModelDoesNotKnow()
    {
        Assert.Throws<NotSupportedException>(() => SherpaOnnxPlugin.QwenLanguageName("bg"));
    }

    [Theory]
    [InlineData("language English<asr_text>Hello there.", "Hello there.")]
    [InlineData("Hello language English<asr_text>there.", "Hello there.")]
    [InlineData("language Chinese Simplified<asr_text>你好", "你好")]
    [InlineData("<asr_text>Hello", "Hello")]
    [InlineData("I speak the language English fluently.", "I speak the language English fluently.")]
    [InlineData("  plain text  ", "plain text")]
    public void StripQwenLanguageMarkers_RemovesOnlyTheExportsMarkers(string raw, string expected)
    {
        Assert.Equal(expected, SherpaOnnxPlugin.StripQwenLanguageMarkers(raw));
    }

    [Fact]
    public void QwenChunks_TileLongAudioInBoundedWindowsWithoutOverlap()
    {
        var audio = Speech(47);

        var chunks = SherpaDecodeCoordinator.PlanQwenChunks(audio);

        // Five windows at least; a cut moved to a quieter moment can add one.
        Assert.InRange(chunks.Count, 5, 6);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(audio.Length, chunks[^1].End);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i].Start, chunks[i].OwnedStart);
            Assert.Equal(chunks[i].End, chunks[i].OwnedEnd);
            Assert.InRange(chunks[i].End - chunks[i].Start, 1, SherpaDecodeCoordinator.QwenMaximumChunkSampleCount);
            if (i > 0)
                Assert.Equal(chunks[i - 1].End, chunks[i].Start);
        }

        Assert.Single(SherpaDecodeCoordinator.PlanQwenChunks(Speech(10)));
    }

    [Fact]
    public void DecodeQwen_SkipsDigitalSilenceAndRaisesQuietWindows()
    {
        // Quiet speech, then a window of digital silence, then quiet speech again.
        var audio = Speech(8, 0.05f).Concat(new float[10 * SampleRate]).Concat(Speech(8, 0.05f)).ToArray();
        var peaks = new List<float>();
        var coordinator = new SherpaDecodeCoordinator(samples =>
        {
            peaks.Add(samples.Max(Math.Abs));
            return new SherpaDecodeChunk($"part{peaks.Count}");
        });

        var result = coordinator.DecodeQwen(audio, CancellationToken.None);

        Assert.Equal("part1 part2", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Empty(result.TokenTimings);
        Assert.All(peaks, peak => Assert.Equal(SherpaDecodeCoordinator.QwenTargetPeak, peak, 3));
    }

    [Fact]
    public void DecodeQwen_LeavesLoudAudioUntouchedAndCapsTheGainOnNearSilence()
    {
        var peaks = new List<float>();
        var coordinator = new SherpaDecodeCoordinator(samples =>
        {
            peaks.Add(samples.Max(Math.Abs));
            return new SherpaDecodeChunk("text");
        });

        coordinator.DecodeQwen(Speech(5, 0.9f), CancellationToken.None);
        coordinator.DecodeQwen(Speech(5, 0.001f), CancellationToken.None);

        Assert.Equal(0.9f, peaks[0], 3);
        Assert.Equal(0.001f * SherpaDecodeCoordinator.MaximumGain, peaks[1], 3);
    }

    [Fact]
    public void DecodeQwen_RetriesAnAudibleWindowThatDecodedToNothing()
    {
        var calls = 0;
        var coordinator = new SherpaDecodeCoordinator(_ => new SherpaDecodeChunk(++calls == 1 ? " " : "recovered"));

        var result = coordinator.DecodeQwen(Speech(5), CancellationToken.None);

        Assert.Equal("recovered", result.Text);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DecodeQwen_StopsBeforeTheNextWindowOnceCancelled()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
        {
            calls++;
            // ReSharper disable once AccessToDisposedClosure -- the decoder runs synchronously inside DecodeQwen, before cts is disposed.
            cts.Cancel();
            return new SherpaDecodeChunk("text");
        });

        Assert.ThrowsAny<OperationCanceledException>(() => coordinator.DecodeQwen(Speech(35), cts.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(new[] { "Hello there.", "How are you?" }, "Hello there. How are you?")]
    [InlineData(new[] { "你好", "世界" }, "你好世界")]
    [InlineData(new[] { "こんにちは。", "元気です" }, "こんにちは。元気です")]
    [InlineData(new[] { "안녕하세요", "반갑습니다" }, "안녕하세요 반갑습니다")]
    [InlineData(new[] { "使用 Linux", "系统" }, "使用 Linux 系统")]
    public void JoinWindows_AddsNoSpaceBetweenChineseOrJapaneseCharacters(string[] parts, string expected)
    {
        Assert.Equal(expected, SherpaDecodeCoordinator.JoinWindows(parts));
    }

    [Fact]
    public void GpuRuntimeInstall_RemovesOnlyOlderRuntimesNobodyIsInstalling()
    {
        var installer = new SherpaCudaRuntimeInstaller(_root, new HttpClient());
        var root = Path.Join(_root, "Runtimes", "sherpa-onnx-cuda");
        var current = Path.Join(root, SherpaCudaRuntimeInstaller.RuntimeVersion);
        var old = Path.Join(root, "v1.12.23");
        var installing = Path.Join(root, "v1.12.20");
        var newer = Path.Join(root, "v99.0.0");
        var unrelated = Path.Join(root, "notes");
        foreach (var directory in new[] { current, old, installing, newer, unrelated })
        {
            Directory.CreateDirectory(Path.Join(directory, "native"));
            File.WriteAllText(Path.Join(directory, "native", "file"), "x");
            File.WriteAllText(Path.Join(directory, "runtime.tar.bz2.lock"), "");
        }

        // Another process holds this one's install lock.
        using (new FileStream(Path.Join(installing, "runtime.tar.bz2.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            installer.RemoveSupersededRuntimes();

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(installing));
        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(newer));
        Assert.True(Directory.Exists(unrelated));
    }

    private static string WriteQwenFiles(string dir)
    {
        foreach (var (name, bytes) in s_qwenContent)
        {
            var path = Path.Join(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        return dir;
    }

    private Mock<IPluginHostServices> CreateHost()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(_root);
        host.Setup(h => h.PluginAssetDirectory).Returns(_root);
        return host;
    }

    private SherpaOnnxPlugin CreateDownloader(FakeRepository server, string modelId)
    {
        var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(CreateHost().Object);
        plugin.SpaceProbe = _ => long.MaxValue;
        plugin.SetDownloadSourceForTests(
            server,
            [
                new SherpaOnnxPlugin.ModelDefinition(
                    modelId,
                    "Qwen Test",
                    "~1 MB",
                    1,
                    30,
                    false,
                    false,
                    false,
                    s_qwenContent
                        .Select(entry => new SherpaOnnxPlugin.ModelFileDefinition(
                            entry.Key,
                            $"{BaseUrl}/{entry.Key}",
                            entry.Value.Length,
                            Convert.ToHexStringLower(SHA256.HashData(entry.Value))
                        ))
                        .ToList(),
                    SherpaOnnxPlugin.SherpaModelFamily.Qwen3Asr
                ),
            ]
        );
        return plugin;
    }

    // A tone at the given peak, so every window has audible content.
    private static float[] Speech(double seconds, float peak = 0.5f)
    {
        var samples = new float[(int)(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = peak * MathF.Sin(i * 0.05f);
        return samples;
    }

    private sealed class ReadyProvisioner(string root) : SherpaCuda.CudaRuntimeProvisioner(root, new HttpClient())
    {
        public override Task EnsureReadyAsync(
            SherpaCuda.CudaRuntimeProfile profile,
            IProgress<double>? progress,
            CancellationToken ct
        ) => Task.CompletedTask;
    }

    private sealed class NoopInstaller(string root) : SherpaCudaRuntimeInstaller(root, new HttpClient())
    {
        public override Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeRepository : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(s_qwenContent[path["/repo/".Length..]]),
                }
            );
        }
    }
}
