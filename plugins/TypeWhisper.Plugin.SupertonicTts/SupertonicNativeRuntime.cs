using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace TypeWhisper.Plugin.SupertonicTts;

/// <summary>
///     ONNX Runtime imports <c>onnxruntime.dll</c>, which nothing in the plugin's load context maps
///     to the packaged <c>libonnxruntime.so</c>; load the copy shipped beside the plugin's assembly.
/// </summary>
internal static class SupertonicNativeRuntime
{
    private static readonly Lock s_sync = new();
    private static bool s_resolverRegistered;

    public static void RegisterResolver()
    {
        lock (s_sync)
        {
            if (s_resolverRegistered)
                return;

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Another component already resolves for this assembly (the host does for its own copy).
            }

            s_resolverRegistered = true;
        }
    }

    internal static IReadOnlyList<string> Candidates(string assemblyDirectory) =>
    [
        // Publish flattens native assets beside the assemblies; a build keeps the runtimes tree.
        Path.Join(assemblyDirectory, "libonnxruntime.so"),
        Path.Join(assemblyDirectory, "runtimes", RuntimeIdentifier, "native", "libonnxruntime.so"),
    ];

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!OperatingSystem.IsLinux() || !libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var directory = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(directory))
            return IntPtr.Zero;

        var path = Candidates(directory).FirstOrDefault(File.Exists);
        return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
    }

    private static string RuntimeIdentifier =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
}
