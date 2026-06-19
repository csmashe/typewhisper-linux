using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
///     Routes the managed sherpa-onnx P/Invokes to the downloaded GPU native
///     build and preloads its ONNX Runtime dependencies.
///     <para>
///         On Linux the managed binding's only direct P/Invoke target is
///         <c>libsherpa-onnx-c-api.so</c>; a <see cref="NativeLibrary.SetDllImportResolver" />
///         redirects that to the GPU directory when CUDA is active. But the GPU
///         libraries carry no rpath, so the transitive dependencies
///         (<c>libonnxruntime.so</c> and the CUDA execution provider) won't be
///         found by the loader on their own. We therefore <c>dlopen</c> them
///         <c>RTLD_GLOBAL</c>, in dependency order, before the C API loads.
///     </para>
/// </summary>
internal static class SherpaOnnxNativeRuntime
{
    private const int RtldNow = 0x002;
    private const int RtldGlobal = 0x100;

    // Loaded RTLD_GLOBAL ahead of the C API so its undefined references resolve.
    // The CUDA math libs (cudart/cublas/cufft/curand/cudnn) are preloaded
    // separately by CudaRuntimeProvisioner before this runs. We also load the ORT
    // CUDA provider here by absolute path: it carries no rpath, the runtime dir is
    // not on the loader search path, and relying on libonnxruntime.so to find it
    // "next to itself" is fragile — so we make it explicit (and surface any missing
    // CUDA dependency now, while Auto can still fall back to CPU).
    private static readonly string[] PreloadOrder =
    [
        "libonnxruntime_providers_shared.so",
        "libonnxruntime.so",
        "libonnxruntime_providers_cuda.so",
        "libsherpa-onnx-cxx-api.so"
    ];

    private static readonly object Sync = new();
    private static bool _resolverRegistered;
    private static string? _cudaRuntimeDirectory;

    /// <summary>
    ///     Registers the import resolver once. Safe (and cheap) to call even on the
    ///     CPU path: until <see cref="ConfigureCudaRuntime" /> runs, the resolver
    ///     defers to the default loader, which picks up the CPU runtime shipped in
    ///     the managed nuget.
    /// </summary>
    public static void RegisterResolver()
    {
        lock (Sync)
        {
            if (_resolverRegistered)
                return;

            NativeLibrary.SetDllImportResolver(
                typeof(OfflineRecognizer).Assembly,
                ResolveNativeLibrary
            );
            _resolverRegistered = true;
        }
    }

    /// <summary>
    ///     Points the resolver at the GPU runtime directory and preloads the ORT
    ///     dependencies. Must be called after the CUDA math libraries are preloaded
    ///     and before the first recognizer is created. Idempotent.
    /// </summary>
    public static void ConfigureCudaRuntime(string runtimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            throw new ArgumentException("Runtime directory is required.", nameof(runtimeDirectory));

        lock (Sync)
        {
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(OfflineRecognizer).Assembly,
                    ResolveNativeLibrary
                );
                _resolverRegistered = true;
            }

            foreach (var soname in PreloadOrder)
            {
                var path = Path.Join(runtimeDirectory, soname);
                if (!File.Exists(path))
                    continue;

                var handle = dlopen(path, RtldNow | RtldGlobal);
                if (handle == IntPtr.Zero)
                {
                    var error = Marshal.PtrToStringAnsi(dlerror());
                    throw new InvalidOperationException(
                        $"Failed to preload sherpa-onnx GPU dependency '{soname}': "
                            + (error ?? "unknown error")
                    );
                }
            }

            // Point the resolver at the GPU dir only after every dependency loaded.
            // If a preload above threw, the resolver stays on the CPU runtime so an
            // Auto fallback gets a genuine CPU load rather than the half-wired GPU one.
            _cudaRuntimeDirectory = runtimeDirectory;
        }
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath
    )
    {
        var runtimeDirectory = _cudaRuntimeDirectory;
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            return IntPtr.Zero; // CPU path: let the default loader find the nuget runtime.

        var fileName = ToSoFileName(libraryName);
        var candidate = Path.Join(runtimeDirectory, fileName);
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
    }

    // The managed binding P/Invokes the bare name "sherpa-onnx-c-api"; map any
    // requested name to its Linux soname form (lib<name>.so) so we can look it up
    // in the GPU directory.
    private static string ToSoFileName(string libraryName)
    {
        var name = Path.GetFileName(libraryName);
        if (!name.StartsWith("lib", StringComparison.Ordinal))
            name = "lib" + name;
        if (!name.Contains(".so", StringComparison.Ordinal))
            name += ".so";
        return name;
    }

    [DllImport("libdl.so.2", CharSet = CharSet.Ansi)]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlerror();
}
