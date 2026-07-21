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
    // separately by CudaRuntimeProvisioner before this runs.
    //
    // IMPORTANT: do NOT add libonnxruntime_providers_cuda.so here. Its ELF
    // constructors call Provider_GetHost() and immediately dereference the result.
    // That ProviderHost is installed by libonnxruntime.so only when ORT loads the
    // provider through its own provider-bridge (ProviderLibrary::Ensure) during
    // session creation — it calls SetProviderHost on the shared lib *before*
    // dlopen'ing the provider. dlopen'ing the CUDA provider ourselves runs those
    // constructors while the host is still null, so Provider_GetHost() returns null
    // and the vtable deref segfaults the whole process (uncatchable by managed
    // try/catch). ORT locates the provider next to libonnxruntime.so — which we DO
    // preload by absolute path, so its directory is discoverable — and loads it
    // correctly (host first) when the recognizer requests the CUDA EP. A genuinely
    // missing CUDA dependency then surfaces as a catchable session-creation error
    // (→ CPU fallback) instead of a crash.
    // internal (not private) so a regression test can assert the CUDA provider is
    // never reintroduced here (see the §6 invariant in the comment above).
    // ReSharper disable once InconsistentNaming -- internal static field is part of the test-observable API; PascalCase intended.
    internal static readonly string[] PreloadOrder =
    [
        "libonnxruntime_providers_shared.so",
        "libonnxruntime.so",
        "libsherpa-onnx-cxx-api.so",
    ];

    private static readonly Lock s_sync = new();
    private static bool s_resolverRegistered;
    private static string? s_cudaRuntimeDirectory;

    /// <summary>
    ///     Registers the import resolver once. Safe (and cheap) to call even on the
    ///     CPU path: until <see cref="ConfigureCudaRuntime" /> runs, the resolver
    ///     defers to the default loader, which picks up the CPU runtime shipped in
    ///     the managed nuget.
    /// </summary>
    public static void RegisterResolver()
    {
        lock (s_sync)
        {
            if (s_resolverRegistered)
                return;

            NativeLibrary.SetDllImportResolver(
                typeof(OfflineRecognizer).Assembly,
                ResolveNativeLibrary
            );
            s_resolverRegistered = true;
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

        lock (s_sync)
        {
            if (!s_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(OfflineRecognizer).Assembly,
                    ResolveNativeLibrary
                );
                s_resolverRegistered = true;
            }

            foreach (var soname in PreloadOrder)
            {
                var path = Path.Join(runtimeDirectory, soname);
                if (!File.Exists(path))
                    continue;

                var handle = dlopen(path, RtldNow | RtldGlobal);
                // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
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
            s_cudaRuntimeDirectory = runtimeDirectory;
        }
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath
    )
    {
        var runtimeDirectory = s_cudaRuntimeDirectory;
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
