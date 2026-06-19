using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Shared.Cuda;

internal enum CudaRuntimeComponent
{
    CudaRuntime,
    Cublas,
    Cufft,
    Curand,
    Cudnn
}

internal sealed class CudaRuntimeProvisioner
{
    private const int RtldNow = 2;
    private const int RtldGlobal = 0x100;
    private const string BundleVersion = "cuda-12.9-cudnn-9.12";

    private static readonly IReadOnlyList<CudaRuntimeComponent> DefaultDependencyOrder =
    [
        CudaRuntimeComponent.CudaRuntime,
        CudaRuntimeComponent.Cublas,
        CudaRuntimeComponent.Cufft,
        CudaRuntimeComponent.Curand,
        CudaRuntimeComponent.Cudnn
    ];

    private static readonly IReadOnlyDictionary<CudaRuntimeComponent, CudaWheelPackage> Packages =
        new Dictionary<CudaRuntimeComponent, CudaWheelPackage>
        {
            [CudaRuntimeComponent.CudaRuntime] = new(
                "nvidia-cuda-runtime-cu12",
                "12.9.79",
                ["libcudart.so.12"]),
            [CudaRuntimeComponent.Cublas] = new(
                "nvidia-cublas-cu12",
                "12.9.2.10",
                ["libcublasLt.so.12", "libcublas.so.12"]),
            [CudaRuntimeComponent.Cufft] = new(
                "nvidia-cufft-cu12",
                "11.4.1.4",
                ["libcufft.so.11"]),
            [CudaRuntimeComponent.Curand] = new(
                "nvidia-curand-cu12",
                "10.3.10.19",
                ["libcurand.so.10"]),
            [CudaRuntimeComponent.Cudnn] = new(
                "nvidia-cudnn-cu12",
                "9.12.0.46",
                [
                    "libcudnn.so.9",
                    "libcudnn_graph.so.9",
                    "libcudnn_ops.so.9",
                    "libcudnn_cnn.so.9",
                    "libcudnn_adv.so.9",
                    "libcudnn_engines_precompiled.so.9",
                    "libcudnn_engines_runtime_compiled.so.9",
                    "libcudnn_heuristic.so.9"
                ])
        };

    private static readonly object s_preloadLock = new();
    private static readonly Dictionary<string, IntPtr> s_preloadedLibraries = new(
        StringComparer.Ordinal);

    private readonly string _bundleDirectory;
    private readonly HttpClient _httpClient;
    private readonly IPluginHostServices? _host;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CudaRuntimeProvisioner(string cacheRoot, HttpClient httpClient, IPluginHostServices? host = null)
    {
        _bundleDirectory = Path.Join(Path.GetFullPath(cacheRoot), "Runtimes", "cuda", BundleVersion);
        _httpClient = httpClient;
        _host = host;
    }

    public async Task PreloadAsync(
        IReadOnlyCollection<CudaRuntimeComponent>? components = null,
        bool allowDownloads = true,
        CancellationToken cancellationToken = default)
    {
        var requested = NormalizeComponents(components);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_bundleDirectory);
            CleanupStaleBundles();

            foreach (var component in requested)
            {
                var package = Packages[component];
                var missing = package.LibraryNames
                    .Where(name => FindLibrary(name) is null)
                    .ToArray();

                if (missing.Length == 0)
                    continue;

                if (!allowDownloads)
                {
                    throw new InvalidOperationException(
                        "CUDA runtime libraries are not installed: "
                        + string.Join(", ", missing));
                }

                await InstallPackageAsync(package, cancellationToken);
            }

            PreloadLibraries(requested);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool HasVisibleLibraries(IReadOnlyCollection<CudaRuntimeComponent>? components = null)
    {
        var requested = NormalizeComponents(components);
        return requested.All(component =>
            Packages[component].LibraryNames.All(name => FindLibrary(name) is not null));
    }

    private static IReadOnlyList<CudaRuntimeComponent> NormalizeComponents(
        IReadOnlyCollection<CudaRuntimeComponent>? components)
    {
        if (components is null || components.Count == 0)
            return DefaultDependencyOrder;

        return DefaultDependencyOrder
            .Where(components.Contains)
            .ToArray();
    }

    private async Task InstallPackageAsync(
        CudaWheelPackage package,
        CancellationToken cancellationToken)
    {
        if (package.LibraryNames.All(name => File.Exists(Path.Join(_bundleDirectory, name))))
            return;

        _host?.Log(
            PluginLogLevel.Info,
            $"Downloading CUDA dependency {package.PackageName} {package.Version}.");

        var wheelUrl = await ResolveWheelUrlAsync(package, cancellationToken);
        var wheelPath = Path.Join(
            _bundleDirectory,
            $"{package.PackageName}-{package.Version}.{Guid.NewGuid():N}.whl.tmp");

        try
        {
            await DownloadFileAsync(wheelUrl, wheelPath, cancellationToken);
            ExtractLibrariesFromWheel(wheelPath, package.LibraryNames, _bundleDirectory);
        }
        finally
        {
            TryDeleteFile(wheelPath);
        }

        var missing = package.LibraryNames
            .Where(name => FindLibrary(name) is null)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"CUDA dependency {package.PackageName} {package.Version} is incomplete. Missing: "
                + string.Join(", ", missing));
        }
    }

    private async Task<string> ResolveWheelUrlAsync(
        CudaWheelPackage package,
        CancellationToken cancellationToken)
    {
        var metadataUrl = $"https://pypi.org/pypi/{package.PackageName}/{package.Version}/json";
        using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (var file in document.RootElement.GetProperty("urls").EnumerateArray())
        {
            var filename = file.GetProperty("filename").GetString();
            if (filename is null
                || !filename.EndsWith("x86_64.whl", StringComparison.OrdinalIgnoreCase)
                || !filename.Contains("manylinux", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.TryGetProperty("packagetype", out var packageType)
                && !string.Equals(packageType.GetString(), "bdist_wheel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = file.GetProperty("url").GetString();
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        throw new InvalidOperationException(
            $"Could not find a Linux x64 wheel for {package.PackageName} {package.Version}.");
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

    private static void ExtractLibrariesFromWheel(
        string wheelPath,
        IReadOnlyCollection<string> libraryNames,
        string destinationDirectory)
    {
        using var archive = ZipFile.OpenRead(wheelPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            if (!libraryNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            var destination = Path.Join(destinationDirectory, entry.Name);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private void PreloadLibraries(IReadOnlyList<CudaRuntimeComponent> components)
    {
        foreach (var component in components)
        {
            foreach (var libraryName in Packages[component].LibraryNames)
            {
                var path = FindLibrary(libraryName)
                    ?? throw new InvalidOperationException(
                        $"CUDA runtime library {libraryName} is not installed.");

                PreloadLibrary(path);
            }
        }
    }

    private static void PreloadLibrary(string path)
    {
        lock (s_preloadLock)
        {
            if (s_preloadedLibraries.ContainsKey(path))
                return;

            var handle = dlopen(path, RtldNow | RtldGlobal);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.PtrToStringAnsi(dlerror());
                throw new InvalidOperationException(
                    $"Could not load CUDA runtime library {path}: {error ?? "unknown error"}");
            }

            s_preloadedLibraries[path] = handle;
        }
    }

    private string? FindLibrary(string libraryName)
    {
        var bundled = Path.Join(_bundleDirectory, libraryName);
        if (File.Exists(bundled))
            return bundled;

        if (TryResolveLibraryWithLdConfig(libraryName) is { } ldConfigPath)
            return ldConfigPath;

        foreach (var directory in EnumerateLibraryDirectories())
        {
            var candidate = Path.Join(directory, libraryName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? TryResolveLibraryWithLdConfig(string libraryName)
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("ldconfig", "-p")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });

            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains(libraryName, StringComparison.Ordinal))
                    continue;

                var marker = "=>";
                var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex < 0)
                    continue;

                var path = line[(markerIndex + marker.Length)..].Trim();
                if (File.Exists(path))
                    return path;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLibraryDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directory in EnumerateLdLibraryPathDirectories())
        {
            if (seen.Add(directory))
                yield return directory;
        }

        foreach (var directory in EnumerateCudaDirectories())
        {
            if (seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateLdLibraryPathDirectories()
    {
        var value = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var entry in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Directory.Exists(entry))
                yield return entry;
        }
    }

    private static IEnumerable<string> EnumerateCudaDirectories()
    {
        var roots = new[]
        {
            "/usr/local/cuda",
            "/usr/local/cuda-12.9",
            "/usr/local/cuda-12.8",
            "/usr/local/cuda-12.7",
            "/usr/local/cuda-12.6",
            "/usr/local/cuda-12.5",
            "/usr/local/cuda-12.4",
            "/usr/local/cuda-12.3",
            "/usr/local/cuda-12.2",
            "/usr/local/cuda-12.1",
            "/usr/local/cuda-12.0"
        };

        foreach (var root in roots)
        {
            foreach (var suffix in new[] { "lib64", "targets/x86_64-linux/lib" })
            {
                var directory = Path.Join(root, suffix);
                if (Directory.Exists(directory))
                    yield return directory;
            }
        }
    }

    private void CleanupStaleBundles()
    {
        var cudaRoot = Directory.GetParent(_bundleDirectory)?.FullName;
        if (cudaRoot is null || !Directory.Exists(cudaRoot))
            return;

        foreach (var directory in Directory.EnumerateDirectories(cudaRoot))
        {
            if (string.Equals(directory, _bundleDirectory, StringComparison.Ordinal))
                continue;

            TryDeleteDirectory(directory);
        }
    }

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
            Debug.WriteLine($"Failed to delete stale CUDA runtime directory '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Failed to delete stale CUDA runtime directory '{path}': {ex.Message}");
        }
    }

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlerror();

    private sealed record CudaWheelPackage(
        string PackageName,
        string Version,
        IReadOnlyList<string> LibraryNames);
}
