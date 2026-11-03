using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace TypeWhisper.Plugin.ParakeetCtc;

internal static class OnnxRuntimeNativeLibrary
{
    private static readonly Lock s_sync = new();
    private static bool s_registered;

    internal static void EnsureRegistered()
    {
        lock (s_sync)
        {
            if (s_registered)
                return;

            // Resolve this plugin's ORT package independently of sherpa's native runtime.
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(SessionOptions).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Another owner of this assembly copy already installed a resolver; its
                // rules (or default probing) apply.
            }
            s_registered = true;
        }
    }

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath
    )
    {
        if (!libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var pluginDirectory = Path.GetDirectoryName(typeof(NemoCtcModel).Assembly.Location);
        if (pluginDirectory is null)
            return IntPtr.Zero;

        var rid =
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";
        // A framework-dependent build keeps the package layout; a RID-specific publish
        // (deploy-linux-plugins.sh) flattens the native library next to the assembly.
        foreach (
            var candidate in new[]
            {
                Path.Join(pluginDirectory, "runtimes", rid, "native", "libonnxruntime.so"),
                Path.Join(pluginDirectory, "libonnxruntime.so"),
            }
        )
        {
            if (File.Exists(candidate))
                return NativeLibrary.Load(candidate);
        }

        return IntPtr.Zero;
    }
}
