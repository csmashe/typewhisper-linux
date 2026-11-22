extern alias SherpaOnnx;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Moq;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxVulkanTests : IDisposable
{
    private const string ModelId = "parakeet-vulkan-test";
    private const string GgufUrl = "https://example.test/repo/model.gguf";

    private static readonly byte[] s_onnx = [0x08, 0x09, 0x3a, 0x02, 0x12, 0x00];
    private static readonly byte[] s_gguf = [.. "GGUF"u8, 3, 0, 0, 0, 1, 2, 3, 4];

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _log = new();
    private readonly string _root = Path.Join(Path.GetTempPath(), "tw-sherpa-vulkan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Catalog_GivesOnlyTheParakeetModelsAPinnedGguf()
    {
        var catalog = SherpaOnnxPlugin.DefaultModelsForTests;

        Assert.Equal(
            ["parakeet-ultra-0.6b", "parakeet-tdt-0.6b"],
            catalog.Where(m => m.GpuFile is not null).Select(m => m.Id)
        );
        Assert.All(
            catalog.Where(m => m.GpuFile is not null),
            model =>
            {
                Assert.EndsWith("-Q8_0.gguf", model.GpuFile!.FileName, StringComparison.Ordinal);
                Assert.EndsWith(model.GpuFile.FileName, model.GpuFile.DownloadUrl, StringComparison.Ordinal);
                Assert.Matches("^[0-9a-f]{64}$", model.GpuFile.Sha256);
                Assert.InRange(model.GpuFile.SizeBytes, 700_000_000, 800_000_000);
            }
        );
        using var plugin = new SherpaOnnxPlugin();
        Assert.Contains(TranscriptionAccelerationBackend.Vulkan, plugin.SupportedAccelerationBackends);
    }

    [Fact]
    public void BindingsUseTheX64LayoutsOfTranscribeH()
    {
        // Sizes from transcribe.h 0.3.1 on x86-64 Linux; the runtime checks them again before the first load.
        Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.ModelLoadParams>());
        Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.SessionParams>());
        Assert.Equal(104, Marshal.SizeOf<TranscribeCppNative.RunParams>());
        Assert.Equal(64, Marshal.SizeOf<TranscribeCppNative.DeviceInfo>());
        Assert.Equal(48, Marshal.SizeOf<TranscribeCppNative.Token>());
        Assert.Equal(24, Marshal.SizeOf<TranscribeCppNative.BackendInitParams>());
    }

    [Fact]
    public void ContractOfAnotherReleaseIsRejectedBeforeTheLibraryLoads()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Join(_root, "contract.json"), """{"version":"0.3.0","header_hash":"0000000000000000"}""");
        var error = Assert.Throws<InvalidDataException>(() => TranscribeCppRuntime.ValidateContract(_root));
        Assert.Contains("0.3.0", error.Message);

        File.WriteAllText(Path.Join(_root, "contract.json"), """{"version":"0.3.1","header_hash":"57b1af43650f195d"}""");
        TranscribeCppRuntime.ValidateContract(_root);
    }

    [Fact]
    public void ModelsGoToTheFirstDedicatedVulkanGpu()
    {
        var cpu = new TranscribeCppDevice(2, "Ryzen", "cpu", 0);
        var integrated = new TranscribeCppDevice(0, "AMD Radeon(TM) Graphics", "vulkan", 2);
        var dedicated = new TranscribeCppDevice(1, "Radeon RX 7800 XT", "vulkan", 1);

        Assert.Equal(dedicated, TranscribeCppRuntime.PreferredVulkanDevice([integrated, dedicated, cpu]));
        Assert.Equal(integrated, TranscribeCppRuntime.PreferredVulkanDevice([integrated, cpu]));
        Assert.Null(TranscribeCppRuntime.PreferredVulkanDevice([cpu]));
    }

    [Fact]
    public void TokensWithLeadingSpacesBecomeTimedWords()
    {
        // transcribe.cpp marks a word start with a space; two tokens can share one frame.
        var result = new TranscribeCppResult(
            "Kubernetes rocks.",
            [
                new TranscribeCppToken(" Ku", .32, .48),
                new TranscribeCppToken("ber", .48, .48),
                new TranscribeCppToken("netes", .64, .96),
                new TranscribeCppToken(" rocks", 1.0, 1.3),
                new TranscribeCppToken(".", 1.3, 1.4),
            ]
        );

        var decoded = new SherpaDecodeCoordinator(_ => SherpaOnnxPlugin.ToDecodeChunk(result)).Decode(
            Samples(seconds: 2),
            false,
            CancellationToken.None
        );

        Assert.Equal("Kubernetes rocks.", decoded.Text);
        Assert.Equal([" Ku", "ber", "netes", " rocks", "."], decoded.TokenTimings.Select(t => t.Text));
        Assert.All(decoded.TokenTimings, t => Assert.True(t.EndSeconds > t.StartSeconds));
        Assert.Equal(.48, decoded.TokenTimings[1].StartSeconds, 6);
    }

    [Fact]
    public async Task VulkanLoad_DownloadsTheGgufAndTranscribesOnTheGraphicsCard()
    {
        var server = new FakeRepository();
        using var plugin = CreatePlugin(server);
        var installer = new FakeVulkanInstaller(_root);
        var session = new FakeGpuSession();
        string? openedModel = null;
        string? openedRuntime = null;
        plugin.SetVulkanDependenciesForTests(installer, (runtime, model) =>
        {
            openedRuntime = runtime;
            openedModel = model;
            return session;
        });
        plugin.SetParakeetRecognizerFactoryForTests((_, _) => throw new InvalidOperationException("sherpa must not load"));
        WriteOnnxFiles();
        var progress = new System.Collections.Concurrent.ConcurrentQueue<double>();

        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);
        Assert.Equal(TranscriptionAccelerationBackend.Vulkan, plugin.AccelerationStatus.ActiveBackend);
        await plugin.LoadModelAsync(ModelId, new SynchronousProgress(progress.Enqueue), CancellationToken.None);

        Assert.Equal(1, installer.Calls);
        Assert.Equal(["/repo/model.gguf"], server.RequestedPaths);
        Assert.Equal(installer.RuntimeDirectory, openedRuntime);
        Assert.Equal(Path.Join(_root, "Models", ModelId, "model.gguf"), openedModel);
        Assert.Equal(s_gguf, await File.ReadAllBytesAsync(openedModel!));
        Assert.Equal(1.0, progress.Last());
        Assert.All(progress, value => Assert.InRange(value, 0.0, 1.0));
        Assert.Equal("vulkan", plugin.ComputeBackendForTests);
        Assert.Equal(
            new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Vulkan, "Using Vulkan", "NVIDIA GeForce GTX 1070"),
            plugin.AccelerationStatus
        );

        var result = await plugin.TranscribeAsync(Wav(seconds: 2), null, false, null, CancellationToken.None);
        Assert.Equal("Hello there.", result.Text);
        Assert.Equal([" Hello", " there", "."], result.TokenTimings.Select(t => t.Text));
        Assert.Equal(1, session.Calls);

        // A second load reuses the downloaded GGUF.
        await plugin.LoadModelAsync(ModelId, CancellationToken.None);
        Assert.Single(server.RequestedPaths);
        Assert.True(session.Disposed);

        await plugin.UnloadModelAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.TranscribeAsync(Wav(seconds: 1), null, false, null, CancellationToken.None));
    }

    [Fact]
    public async Task VulkanLoad_FallsBackToTheCpuWhenTheGraphicsCardCannotStart()
    {
        using var plugin = CreatePlugin(new FakeRepository());
        plugin.SetVulkanDependenciesForTests(
            new FakeVulkanInstaller(_root),
            (_, _) => throw new InvalidOperationException("No Vulkan GPU found.")
        );
        string? provider = null;
        plugin.SetParakeetRecognizerFactoryForTests((_, requested) =>
        {
            provider = requested;
            throw new InvalidOperationException("stop before the native runtime");
        });
        WriteOnnxFiles();
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(ModelId, CancellationToken.None));

        Assert.Equal("cpu", provider);
        Assert.Equal("vulkan", plugin.ComputeBackendForTests);
    }

    [Fact]
    public async Task VulkanLoad_FallsBackBeforeAnyDownloadWithoutAVulkanGpu()
    {
        var server = new FakeRepository();
        using var plugin = CreatePlugin(server);
        var installer = new FakeVulkanInstaller(_root);
        plugin.SetVulkanDependenciesForTests(installer, (_, _) => throw new InvalidOperationException("must not open"), () => "No Vulkan GPU found.");
        string? provider = null;
        plugin.SetParakeetRecognizerFactoryForTests((_, requested) =>
        {
            provider = requested;
            throw new InvalidOperationException("stop before the native runtime");
        });
        WriteOnnxFiles();
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(ModelId, CancellationToken.None));

        Assert.Equal("cpu", provider);
        Assert.Equal(0, installer.Calls);
        Assert.Empty(server.RequestedPaths);
    }

    [Fact]
    public async Task VulkanLoad_FallsBackToTheCpuWhenTheRuntimeDoesNotDownload()
    {
        var server = new FakeRepository();
        using var plugin = CreatePlugin(server);
        var installer = new FakeVulkanInstaller(_root) { Failure = new HttpRequestException("offline") };
        plugin.SetVulkanDependenciesForTests(installer, (_, _) => throw new InvalidOperationException("must not open"));
        string? provider = null;
        plugin.SetParakeetRecognizerFactoryForTests((_, requested) =>
        {
            provider = requested;
            throw new InvalidOperationException("stop before the native runtime");
        });
        WriteOnnxFiles();
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(ModelId, CancellationToken.None));

        Assert.Equal("cpu", provider);
        Assert.Empty(server.RequestedPaths);
        Assert.Equal("vulkan", plugin.ComputeBackendForTests);
    }

    [Fact]
    public async Task VulkanLoad_ReplacesAGgufThatIsNotOne()
    {
        var server = new FakeRepository();
        using var plugin = CreatePlugin(server);
        plugin.SetVulkanDependenciesForTests(new FakeVulkanInstaller(_root), (_, _) => new FakeGpuSession());
        WriteOnnxFiles();
        var gguf = Path.Join(_root, "Models", ModelId, "model.gguf");
        await File.WriteAllBytesAsync(gguf, new byte[s_gguf.Length]);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);

        plugin.SetParakeetRecognizerFactoryForTests((_, _) => throw new InvalidOperationException("stop before the native runtime"));

        // The same-size file passes the download check but fails the header check at open.
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(ModelId, CancellationToken.None));
        Assert.False(File.Exists(gguf));

        // Only that load fell back. A worker never hears the preference again, so the next load retries Vulkan.
        Assert.Equal("vulkan", plugin.ComputeBackendForTests);
        await plugin.LoadModelAsync(ModelId, CancellationToken.None);
        Assert.Equal(TranscriptionAccelerationBackend.Vulkan, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal(s_gguf, await File.ReadAllBytesAsync(gguf));
    }

    [Fact]
    public async Task VulkanLoad_ReplacesADamagedGgufThatTheGraphicsCardRejects()
    {
        var server = new FakeRepository();
        using var plugin = CreatePlugin(server);
        var opens = 0;
        plugin.SetVulkanDependenciesForTests(new FakeVulkanInstaller(_root), (_, model) =>
        {
            opens++;
            return File.ReadAllBytes(model).SequenceEqual(s_gguf)
                ? new FakeGpuSession()
                : throw new InvalidOperationException("transcribe.cpp could not load the model: invalid model");
        });
        WriteOnnxFiles();
        var gguf = Path.Join(_root, "Models", ModelId, "model.gguf");
        byte[] damaged = [.. s_gguf];
        damaged[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(gguf, damaged);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);
        plugin.SetParakeetRecognizerFactoryForTests((_, _) => throw new InvalidOperationException("stop before the native runtime"));

        // Same size and GGUF header: only the pin catches it, after the native open fails.
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(ModelId, CancellationToken.None));
        Assert.False(File.Exists(gguf));

        await plugin.LoadModelAsync(ModelId, CancellationToken.None);
        Assert.Equal(2, opens);
        Assert.Equal(TranscriptionAccelerationBackend.Vulkan, plugin.AccelerationStatus.ActiveBackend);
        Assert.Equal(s_gguf, await File.ReadAllBytesAsync(gguf));
    }

    [Fact]
    public async Task VulkanLoad_KeepsAnIntactGgufWhenTheGraphicsCardFails()
    {
        using var plugin = CreatePlugin(new FakeRepository());
        plugin.SetVulkanDependenciesForTests(
            new FakeVulkanInstaller(_root),
            (_, _) => throw new InvalidOperationException("transcribe.cpp could not load the model: out of memory")
        );
        WriteOnnxFiles();
        var gguf = Path.Join(_root, "Models", ModelId, "model.gguf");
        await File.WriteAllBytesAsync(gguf, s_gguf);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);
        plugin.SetParakeetRecognizerFactoryForTests((_, _) => throw new InvalidOperationException("stop before the native runtime"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync(ModelId, CancellationToken.None));
        Assert.True(File.Exists(gguf));
    }

    [Fact]
    public async Task VulkanChoice_RunsQwenOnTheCpu()
    {
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(CreateHost().Object);
        plugin.SpaceProbe = _ => long.MaxValue;
        string? provider = null;
        plugin.SetQwenRecognizerFactoryForTests((_, requested) =>
        {
            provider = requested;
            throw new InvalidOperationException("stop before the native runtime");
        });
        var dir = Path.Join(_root, "Models", SherpaOnnxPlugin.Qwen06BModelId);
        foreach (var (name, bytes) in new Dictionary<string, byte[]>
                 {
                     ["conv_frontend.onnx"] = s_onnx,
                     ["encoder.int8.onnx"] = s_onnx,
                     ["decoder.int8.onnx"] = s_onnx,
                     ["tokenizer/merges.txt"] = "#version: 0.2\nĠ t\n"u8.ToArray(),
                     ["tokenizer/vocab.json"] = """{"a": 0}"""u8.ToArray(),
                     ["tokenizer/tokenizer_config.json"] = """{"model_max_length": 131072}"""u8.ToArray(),
                 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Join(dir, name))!);
            await File.WriteAllBytesAsync(Path.Join(dir, name), bytes);
        }
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.LoadModelAsync(SherpaOnnxPlugin.Qwen06BModelId, CancellationToken.None));

        Assert.Equal("cpu", provider);
        Assert.Equal("vulkan", plugin.ComputeBackendForTests);
    }

    [Fact]
    public async Task RuntimeArchive_KeepsOnlyLibrariesContractAndLicenses()
    {
        var archive = Path.Join(_root, "runtime.tar.gz");
        Directory.CreateDirectory(_root);
        await WriteArchiveAsync(
            archive,
            [
                ("transcribe-native-linux-x86_64-cpu-vulkan/contract.json", "{}"),
                ("transcribe-native-linux-x86_64-cpu-vulkan/libtranscribe.so", "elf"),
                ("transcribe-native-linux-x86_64-cpu-vulkan/licenses/ggml/LICENSE", "MIT"),
                ("transcribe-native-linux-x86_64-cpu-vulkan/include/transcribe.h", "header"),
                ("transcribe-native-linux-x86_64-cpu-vulkan/bin/libnested.so", "nested"),
                ("README.md", "outside the root"),
            ]
        );
        var destination = Path.Join(_root, "native");

        await TranscribeCppRuntimeInstaller.InstallFromArchiveAsync(archive, destination, CancellationToken.None);

        Assert.Equal(
            ["contract.json", "libtranscribe.so", "licenses/ggml/LICENSE"],
            Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(destination, path))
                .Order(StringComparer.Ordinal)
        );
        Assert.Empty(Directory.GetDirectories(_root, "native.staging-*"));
    }

    [Theory]
    [InlineData("transcribe-native-linux-x86_64-cpu-vulkan/../escape.so")]
    [InlineData("/transcribe-native-linux-x86_64-cpu-vulkan/libtranscribe.so")]
    public async Task RuntimeArchive_RejectsUnsafeEntriesAndInstallsNothing(string entry)
    {
        var archive = Path.Join(_root, "runtime.tar.gz");
        Directory.CreateDirectory(_root);
        await WriteArchiveAsync(archive, [(entry, "elf")]);
        var destination = Path.Join(_root, "native");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranscribeCppRuntimeInstaller.InstallFromArchiveAsync(archive, destination, CancellationToken.None));

        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(_root, "native.staging-*"));
        Assert.False(File.Exists(Path.Join(_root, "escape.so")));
    }

    [Fact]
    public void RuntimeArchive_WithTheWrongBytesIsRejected()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, TranscribeCppRuntimeInstaller.AssetFileName);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => TranscribeCppRuntimeInstaller.VerifyArchive(path));
    }

    // Runs the real runtime and GGUF on this machine's graphics card when both are given:
    // TW_TRANSCRIBE_CPP_RUNTIME (the extracted archive), TW_PARAKEET_GGUF and TW_PARAKEET_WAV (16 kHz mono).
    [LocalTranscribeCppFact]
    public async Task LocalVulkanLoad_TranscribesSpeechOnTheGraphicsCard()
    {
        var runtime = Environment.GetEnvironmentVariable("TW_TRANSCRIBE_CPP_RUNTIME")!;
        var gguf = Environment.GetEnvironmentVariable("TW_PARAKEET_GGUF")!;
        var wav = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("TW_PARAKEET_WAV")!);

        // A hard link beside the GGUF avoids copying 740 MB; a symbolic link reports its own length.
        var root = Path.Join(Path.GetDirectoryName(gguf)!, "tw-sherpa-vulkan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var installer = new FakeVulkanInstaller(root);
            var model = Path.Join(root, "Models", ModelId);
            Directory.CreateDirectory(model);
            HardLink(gguf, Path.Join(model, "model.gguf"));
            foreach (var file in Directory.EnumerateFileSystemEntries(runtime))
                CopyTree(file, Path.Join(installer.RuntimeDirectory, Path.GetFileName(file)));
            using var plugin = CreatePlugin(new FakeRepository(), new FileInfo(gguf).Length, Sha256(gguf), root);
            plugin.SetVulkanDependenciesForTests(installer, null);
            plugin.SetParakeetRecognizerFactoryForTests((_, _) =>
                throw new InvalidOperationException("fell back to the CPU: " + string.Join(" | ", _log)));
            WriteOnnxFiles(root);
            plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Vulkan);

            await plugin.LoadModelAsync(ModelId, CancellationToken.None);
            var result = await plugin.TranscribeAsync(wav, null, false, null, CancellationToken.None);

            Assert.Equal(TranscriptionAccelerationBackend.Vulkan, plugin.AccelerationStatus.ActiveBackend);
            Assert.False(string.IsNullOrWhiteSpace(plugin.AccelerationStatus.Detail));
            Assert.StartsWith("He hoped there would be stew for dinner", result.Text, StringComparison.Ordinal);
            Assert.NotEmpty(result.TokenTimings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void HardLink(string source, string destination)
    {
        using var ln = System.Diagnostics.Process.Start("ln", [source, destination]);
        ln.WaitForExit();
        Assert.Equal(0, ln.ExitCode);
    }

    private SherpaOnnxPlugin CreatePlugin(FakeRepository server, long? ggufSize = null, string? ggufSha = null, string? root = null)
    {
        var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(CreateHost(root).Object);
        plugin.SpaceProbe = _ => long.MaxValue;
        plugin.SetDownloadSourceForTests(
            server,
            [
                new SherpaOnnxPlugin.ModelDefinition(
                    ModelId,
                    "Parakeet Test",
                    "~1 MB",
                    1,
                    25,
                    false,
                    false,
                    true,
                    [
                        .. new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx" }.Select(name =>
                            new SherpaOnnxPlugin.ModelFileDefinition(name, "https://example.test/" + name, s_onnx.Length, Sha256(s_onnx))
                        ),
                        new SherpaOnnxPlugin.ModelFileDefinition("tokens.txt", "https://example.test/tokens.txt", 8, ""),
                    ],
                    GpuFile: new SherpaOnnxPlugin.ModelFileDefinition(
                        "model.gguf",
                        GgufUrl,
                        ggufSize ?? s_gguf.Length,
                        ggufSha ?? Sha256(s_gguf)
                    )
                ),
            ]
        );
        return plugin;
    }

    private void WriteOnnxFiles(string? root = null)
    {
        var dir = Path.Join(root ?? _root, "Models", ModelId);
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx" })
            File.WriteAllBytes(Path.Join(dir, name), s_onnx);
        File.WriteAllText(Path.Join(dir, "tokens.txt"), "<blk> 0\n");
    }

    private Mock<IPluginHostServices> CreateHost(string? root = null)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(root ?? _root);
        host.Setup(h => h.PluginAssetDirectory).Returns(root ?? _root);
        host.Setup(h => h.Log(It.IsAny<PluginLogLevel>(), It.IsAny<string>()))
            .Callback<PluginLogLevel, string>((_, message) => _log.Enqueue(message));
        return host;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void CopyTree(string source, string destination)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            CopyTree(entry, Path.Join(destination, Path.GetFileName(entry)));
    }

    private static float[] Samples(double seconds)
    {
        var samples = new float[(int)(seconds * SherpaDecodeCoordinator.SampleRate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.25f * MathF.Sin(i * 0.05f);
        return samples;
    }

    private static byte[] Wav(double seconds)
    {
        var samples = (int)(seconds * SherpaDecodeCoordinator.SampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SherpaDecodeCoordinator.SampleRate);
        writer.Write(SherpaDecodeCoordinator.SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            writer.Write((short)(8000 * Math.Sin(i * 0.05)));
        return stream.ToArray();
    }

    private static async Task WriteArchiveAsync(string path, IEnumerable<(string Name, string Content)> entries)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (name, content) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            };
            await tar.WriteEntryAsync(entry);
        }
    }

    private sealed class FakeVulkanInstaller(string root) : TranscribeCppRuntimeInstaller(root, new HttpClient())
    {
        public Exception? Failure { get; init; }

        public int Calls { get; private set; }

        public override Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct)
        {
            Calls++;
            if (Failure is not null)
                throw Failure;
            progress?.Report(1.0);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGpuSession : IParakeetGpuSession
    {
        public int Calls { get; private set; }

        public bool Disposed { get; private set; }

        // ReSharper disable once ReplaceAutoPropertyWithComputedProperty -- a computed getter reads no instance data and trips CA1822 on this interface member.
        public string DeviceName { get; } = "NVIDIA GeForce GTX 1070";

        public TranscribeCppResult Transcribe(float[] samples, CancellationToken cancellationToken)
        {
            Calls++;
            return new TranscribeCppResult("Hello there.", [
                new TranscribeCppToken(" Hello", .2, .6),
                new TranscribeCppToken(" there", .7, 1.1),
                new TranscribeCppToken(".", 1.1, 1.2),
            ]);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeRepository : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(s_gguf) });
        }
    }
}

public sealed class LocalTranscribeCppFactAttribute : FactAttribute
{
    private static readonly string[] s_variables = ["TW_TRANSCRIBE_CPP_RUNTIME", "TW_PARAKEET_GGUF", "TW_PARAKEET_WAV"];

    public LocalTranscribeCppFactAttribute()
    {
        if (
            s_variables.Any(name =>
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        )
            Skip = "Set TW_TRANSCRIBE_CPP_RUNTIME, TW_PARAKEET_GGUF and TW_PARAKEET_WAV to run on a real graphics card.";
    }
}
