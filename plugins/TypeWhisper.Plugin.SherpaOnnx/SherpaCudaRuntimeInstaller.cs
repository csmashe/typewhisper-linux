using System.Diagnostics;
using System.IO.Compression;
using SharpCompress.Readers;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal interface ISherpaCudaRuntimeInstaller
{
    bool IsInstalled { get; }
    string RuntimeDirectory { get; }
    Task EnsureInstalledAsync(CancellationToken cancellationToken);
}

internal sealed class SherpaCudaRuntimeInstaller : ISherpaCudaRuntimeInstaller
{
    internal const string RuntimeVersion = "v1.12.23";
    internal const string AssetFileName =
        "sherpa-onnx-v1.12.23-cuda-12.x-cudnn-9.x-linux-x64-gpu.tar.bz2";
    internal const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.12.23/" + AssetFileName;

    private static readonly string[] CoreRuntimeFiles =
    [
        "libsherpa-onnx-c-api.so",
        "libonnxruntime.so",
        "libonnxruntime_providers_cuda.so"
    ];

    private static readonly string[] OptionalRuntimeFiles =
    [
        "libonnxruntime_providers_shared.so"
    ];

    private readonly HttpClient _httpClient;
    private readonly string _runtimeRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SherpaCudaRuntimeInstaller(string pluginDataDirectory, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _runtimeRoot = Path.Join(
            Path.GetFullPath(pluginDataDirectory),
            "Runtimes",
            "sherpa-onnx-cuda",
            RuntimeVersion);
    }

    public string RuntimeDirectory => Path.Join(_runtimeRoot, "native");

    public bool IsInstalled => CoreRuntimeFiles.All(file => File.Exists(GetRuntimeFilePath(file)));

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (IsInstalled)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled)
                return;

            Directory.CreateDirectory(_runtimeRoot);
            Directory.CreateDirectory(RuntimeDirectory);
            await InstallSherpaRuntimeAsync(cancellationToken);
            ValidateInstalledRuntime();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InstallSherpaRuntimeAsync(CancellationToken cancellationToken)
    {
        var tempRoot = Path.Join(_runtimeRoot, $"extract-{Guid.NewGuid():N}");
        var archivePath = Path.Join(_runtimeRoot, $"{AssetFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await DownloadFileAsync(DownloadUrl, archivePath, cancellationToken);
            Directory.CreateDirectory(tempRoot);
            ExtractTarBz2(archivePath, tempRoot);

            var nativeSource = FindNativeRuntimeDirectory(tempRoot)
                ?? throw new InvalidOperationException(
                    "The downloaded sherpa-onnx CUDA runtime did not contain libsherpa-onnx-c-api.so.");

            foreach (var fileName in CoreRuntimeFiles.Concat(OptionalRuntimeFiles))
            {
                var source = Path.Join(nativeSource, fileName);
                if (!File.Exists(source))
                    continue;

                File.Copy(source, GetRuntimeFilePath(fileName), overwrite: true);
            }
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void ExtractTarBz2(string archivePath, string destinationDirectory)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(stream);
        var destinationRoot = Path.GetFullPath(destinationDirectory);

        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
                continue;

            var entryKey = reader.Entry.Key;
            if (string.IsNullOrWhiteSpace(entryKey) || Path.IsPathRooted(entryKey))
                throw new InvalidOperationException(
                    $"The sherpa-onnx CUDA runtime archive contains an unsafe path: {entryKey}");

            var destinationPath = Path.GetFullPath(Path.Join(destinationRoot, entryKey));
            if (!destinationPath.StartsWith(
                    destinationRoot + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The sherpa-onnx CUDA runtime archive contains an unsafe path: {entryKey}");
            }

            reader.WriteEntryToDirectory(
                destinationRoot,
                new SharpCompress.Common.ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
        }
    }

    private static string? FindNativeRuntimeDirectory(string rootDirectory) =>
        Directory
            .EnumerateFiles(rootDirectory, "libsherpa-onnx-c-api.so", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

    private void ValidateInstalledRuntime()
    {
        var missing = CoreRuntimeFiles
            .Where(file => !File.Exists(GetRuntimeFilePath(file)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "The sherpa-onnx CUDA runtime is incomplete. Missing: "
                + string.Join(", ", missing));
        }
    }

    private string GetRuntimeFilePath(string fileName) => Path.Join(RuntimeDirectory, fileName);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Failed to delete temporary file '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to delete temporary file '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Failed to delete temporary directory '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to delete temporary directory '{path}': {ex.Message}");
        }
    }
}
