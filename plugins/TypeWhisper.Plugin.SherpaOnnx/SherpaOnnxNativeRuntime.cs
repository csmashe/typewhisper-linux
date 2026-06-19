using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal static class SherpaOnnxNativeRuntime
{
    private const int RtldNow = 2;
    private const int RtldGlobal = 0x100;

    private static readonly object Sync = new();
    private static readonly List<IntPtr> Handles = [];
    private static bool _resolverRegistered;
    private static string? _cudaRuntimeDirectory;

    public static void RegisterResolver()
    {
        lock (Sync)
            RegisterResolverUnsafe();
    }

    public static void ConfigureCudaRuntime(string runtimeDirectory)
    {
        lock (Sync)
        {
            RegisterResolverUnsafe();
            _cudaRuntimeDirectory = runtimeDirectory;
            PrependToLdLibraryPath(runtimeDirectory);
            PreloadRuntimeUnsafe(runtimeDirectory);
        }
    }

    private static void RegisterResolverUnsafe()
    {
        if (_resolverRegistered)
            return;

        NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, ResolveNativeLibrary);
        _resolverRegistered = true;
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        var runtimeDirectory = _cudaRuntimeDirectory;
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            return IntPtr.Zero;

        foreach (var candidateName in GetLinuxLibraryCandidates(libraryName))
        {
            var candidate = Path.Join(runtimeDirectory, candidateName);
            if (File.Exists(candidate))
                return NativeLibrary.Load(candidate);
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> GetLinuxLibraryCandidates(string libraryName)
    {
        var fileName = Path.GetFileName(libraryName);
        yield return fileName;

        if (!fileName.EndsWith(".so", StringComparison.Ordinal)
            && !fileName.Contains(".so.", StringComparison.Ordinal))
        {
            yield return fileName + ".so";
        }

        if (!fileName.StartsWith("lib", StringComparison.Ordinal))
        {
            yield return "lib" + fileName;
            if (!fileName.EndsWith(".so", StringComparison.Ordinal)
                && !fileName.Contains(".so.", StringComparison.Ordinal))
            {
                yield return "lib" + fileName + ".so";
            }
        }
    }

    private static void PreloadRuntimeUnsafe(string runtimeDirectory)
    {
        foreach (var libraryName in new[]
                 {
                     "libonnxruntime.so",
                     "libonnxruntime_providers_shared.so",
                     "libonnxruntime_providers_cuda.so",
                     "libsherpa-onnx-c-api.so"
                 })
        {
            var path = Path.Join(runtimeDirectory, libraryName);
            if (!File.Exists(path))
                continue;

            var handle = dlopen(path, RtldNow | RtldGlobal);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.PtrToStringAnsi(dlerror());
                throw new InvalidOperationException(
                    $"Could not load sherpa-onnx CUDA runtime library {path}: {error ?? "unknown error"}");
            }

            Handles.Add(handle);
        }
    }

    private static void PrependToLdLibraryPath(string runtimeDirectory)
    {
        var current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
        var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry => string.Equals(entry, runtimeDirectory, StringComparison.Ordinal)))
            return;

        Environment.SetEnvironmentVariable(
            "LD_LIBRARY_PATH",
            string.IsNullOrWhiteSpace(current)
                ? runtimeDirectory
                : runtimeDirectory + Path.PathSeparator + current);
    }

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlerror();
}
