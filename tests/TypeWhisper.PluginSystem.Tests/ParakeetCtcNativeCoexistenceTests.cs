using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.ParakeetCtc;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ParakeetCtcNativeCoexistenceTests
{
    [Fact]
    [Trait("Category", "Native")]
    public void SherpaAndManagedOnnxRuntimeCoexist()
    {
        if (
            !OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64
        )
            return;

        var nativeDirectory = Path.Join(
            AppContext.BaseDirectory,
            "runtimes",
            "linux-x64",
            "native"
        );
        // Preload sherpa's SONAME before its C API binds; the flat test output otherwise supplies ORT 1.24.
        var sherpaRuntime = NativeLibrary.Load(
            Path.Join(nativeDirectory, "sherpa", "libonnxruntime.so")
        );
        try
        {
            var getApiBase = Marshal.GetDelegateForFunctionPointer<GetPointer>(
                NativeLibrary.GetExport(sherpaRuntime, "OrtGetApiBase")
            );
            var getVersion = Marshal.GetDelegateForFunctionPointer<GetPointer>(
                Marshal.ReadIntPtr(getApiBase(), IntPtr.Size)
            );
            Assert.StartsWith("1.23", Marshal.PtrToStringUTF8(getVersion()));

            var sherpa = NativeLibrary.Load(Path.Join(nativeDirectory, "libsherpa-onnx-c-api.so"));
            try
            {
                // Use the loader's isolation rules so the host's resolver registration remains independent.
                var testAssemblyPath = typeof(ParakeetCtcNativeCoexistenceTests).Assembly.Location;
                var context = new PluginAssemblyLoadContext(testAssemblyPath);
                try
                {
                    var isolatedTests = context.LoadFromAssemblyPath(testAssemblyPath);
                    var probe = isolatedTests
                        .GetType(typeof(ParakeetCtcNativeCoexistenceTests).FullName!)!
                        .GetMethod(
                            nameof(CreateSessionAndGetVersion),
                            BindingFlags.Static | BindingFlags.NonPublic
                        )!;
                    Assert.StartsWith("1.24", (string)probe.Invoke(null, null)!);
                }
                finally
                {
                    context.Unload();
                }
            }
            finally
            {
                NativeLibrary.Free(sherpa);
            }
        }
        finally
        {
            NativeLibrary.Free(sherpaRuntime);
        }
    }

    private static string CreateSessionAndGetVersion()
    {
        OnnxRuntimeNativeLibrary.EnsureRegistered();
        using var options = new SessionOptions();
        return OrtEnv.Instance().GetVersionString();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetPointer();
}
