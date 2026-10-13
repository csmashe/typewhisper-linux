using System.Security.Cryptography;
using SharpCompress.Readers;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
/// Downloads the AMD MIGraphX ONNX Runtime libraries on first use. ROCm/MIGraphX and
/// the AMD driver remain system prerequisites; the matching ORT build is cached here.
/// </summary>
internal sealed class SherpaMigraphxRuntimeInstaller
{
    internal const string WheelName =
        "onnxruntime_migraphx-1.23.2-cp312-cp312-manylinux_2_27_x86_64.manylinux_2_28_x86_64.whl";
    internal const string DownloadUrl =
        "https://repo.radeon.com/rocm/manylinux/rocm-rel-7.2.1/" + WheelName;
    internal const string WheelSha256 =
        "663bff4dc3f72582d69f12ad073eb5695dfb526d574376cc8e5b161c7d2f0f08";

    internal static readonly string[] RuntimeFiles =
    [
        "libonnxruntime.so.1.23.2",
        "libonnxruntime_providers_shared.so",
        "libonnxruntime_providers_migraphx.so",
    ];

    private const long ApproxDownloadBytes = 20_650_000;
    private const long ExtractedRuntimeBytes = 100_000_000;
    private readonly string _runtimeRoot;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SherpaMigraphxRuntimeInstaller(string pluginDataRoot, HttpClient httpClient)
    {
        _runtimeRoot = Path.Join(pluginDataRoot, "Runtimes", "sherpa-onnx-migraphx", "1.23.2");
        _httpClient = httpClient;
    }

    public string RuntimeDirectory => Path.Join(_runtimeRoot, "native");
    public bool IsInstalled => RuntimeFiles.All(file => File.Exists(Path.Join(RuntimeDirectory, file)));

    public async Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (IsInstalled)
        {
            EnsureRuntimeAlias("libonnxruntime.so.1", "libonnxruntime.so.1.23.2");
            progress?.Report(1);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled)
            {
                EnsureRuntimeAlias("libonnxruntime.so.1", "libonnxruntime.so.1.23.2");
                return;
            }

            Directory.CreateDirectory(RuntimeDirectory);
            var wheelPath = Path.Join(_runtimeRoot, WheelName);
            await using (await InterProcessFileLock.AcquireAsync(wheelPath + ".lock", ct).ConfigureAwait(false))
            {
                if (!IsInstalled)
                {
                    await ResilientDownloader.DownloadToFileAsync(
                        _httpClient,
                        DownloadUrl,
                        wheelPath,
                        ApproxDownloadBytes,
                        TimeSpan.FromSeconds(60),
                        allowResume: true,
                        onBytesOnDisk: bytes => progress?.Report(Math.Min(1, (double)bytes / ApproxDownloadBytes)),
                        verifyComplete: VerifyWheel,
                        ct,
                        new DownloadSpaceRequirement("the sherpa-onnx AMD MIGraphX runtime", ExtractedRuntimeBytes)
                    ).ConfigureAwait(false);

                    try
                    {
                        ExtractRuntimeFiles(wheelPath);
                    }
                    finally
                    {
                        try { File.Delete(wheelPath); } catch (IOException) { }
                    }
                }
            }

            if (!IsInstalled)
                throw new InvalidDataException("The AMD MIGraphX runtime archive did not contain all required ONNX Runtime libraries.");
            EnsureRuntimeAlias("libonnxruntime.so.1", "libonnxruntime.so.1.23.2");
            progress?.Report(1);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureRuntimeAlias(string alias, string target)
    {
        var aliasPath = Path.Join(RuntimeDirectory, alias);
        if (!File.Exists(aliasPath))
            File.CreateSymbolicLink(aliasPath, target);
    }

    private static void VerifyWheel(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (hash != WheelSha256)
            throw new InvalidDataException($"AMD MIGraphX runtime checksum mismatch (expected {WheelSha256}, got {hash}).");
    }

    private void ExtractRuntimeFiles(string wheelPath)
    {
        var wanted = RuntimeFiles.ToHashSet(StringComparer.Ordinal);
        using var stream = File.OpenRead(wheelPath);
        using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory || reader.Entry.Key is null)
                continue;
            var fileName = Path.GetFileName(reader.Entry.Key);
            if (!wanted.Contains(fileName))
                continue;
            using var input = reader.OpenEntryStream();
            using var output = File.Create(Path.Join(RuntimeDirectory, fileName));
            input.CopyTo(output);
            wanted.Remove(fileName);
        }
    }
}
