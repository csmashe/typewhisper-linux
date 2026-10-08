using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
///     Reads token durations from the native offline result.
///     <para>
///         The managed binding's result struct (sherpa-onnx 1.12.23, unchanged on master)
///         declares <c>Durations</c> right after <c>Tokens</c>, where the native
///         <c>SherpaOnnxOfflineRecognizerResult</c> has <c>tokens_arr</c>. Its
///         <see cref="OfflineRecognizerResult.Durations" /> is therefore halves of heap
///         pointers, not seconds. The native struct carries the real durations further on.
///     </para>
/// </summary>
internal static class SherpaNativeDurations
{
    // Native SherpaOnnxOfflineRecognizerResult (c-api.h): text, timestamps, count, tokens,
    // tokens_arr, json, lang, emotion, event, durations, ys_log_probs.
    private const int TimestampsOffset = 8;
    private const int CountOffset = 16;
    private const int DurationsOffset = 72;

    private static readonly Lock s_sync = new();
    private static bool s_resolved;
    private static GetResult? s_getResult;
    private static DestroyResult? s_destroyResult;
    private static readonly FieldInfo? s_streamHandle = typeof(OfflineStream).GetField(
        "_handle",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetResult(IntPtr stream);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyResult(IntPtr result);

    /// <summary>
    ///     Returns the native durations for <paramref name="stream" />'s decoded result, or
    ///     null when they are absent or the native layout does not match the managed result.
    /// </summary>
    internal static float[]? Read(OfflineStream stream, float[]? managedTimestamps)
    {
        if (managedTimestamps is null || managedTimestamps.Length == 0 || !TryInitialize())
            return null;

        if (s_streamHandle!.GetValue(stream) is not HandleRef { Handle: var handle } || handle == IntPtr.Zero)
            return null;

        var result = s_getResult!(handle);
        if (result == IntPtr.Zero)
            return null;
        try
        {
            var count = Marshal.ReadInt32(result, CountOffset);
            var timestamps = Marshal.ReadIntPtr(result, TimestampsOffset);
            var durations = Marshal.ReadIntPtr(result, DurationsOffset);
            if (count != managedTimestamps.Length || timestamps == IntPtr.Zero || durations == IntPtr.Zero)
                return null;

            var nativeTimestamps = new float[count];
            var values = new float[count];
            Marshal.Copy(timestamps, nativeTimestamps, 0, count);
            Marshal.Copy(durations, values, 0, count);
            return Verify(managedTimestamps, nativeTimestamps, values);
        }
        finally
        {
            s_destroyResult!(result);
        }
    }

    // The timestamps the binding did read correctly must match, element for element, or
    // the offsets above do not describe this native build.
    internal static float[]? Verify(float[] managedTimestamps, float[] nativeTimestamps, float[] durations)
    {
        if (
            nativeTimestamps.Length != managedTimestamps.Length
            || durations.Length != managedTimestamps.Length
            || !nativeTimestamps.AsSpan().SequenceEqual(managedTimestamps)
        )
            return null;

        return durations.All(duration => float.IsFinite(duration) && duration >= 0)
            ? durations
            : null;
    }

    private static bool TryInitialize()
    {
        lock (s_sync)
        {
            // The binding stays bound to one library for the process's lifetime.
            if (s_resolved)
                return s_getResult is not null;
            if (s_streamHandle?.FieldType != typeof(HandleRef))
                return false;

            // The stream must go back to the library that created it, so only the copy
            // the binding is already bound to will do.
            var library = SherpaOnnxNativeRuntime.GetLoadedCApi();
            if (library == IntPtr.Zero)
                return false;
            s_resolved = true;

            if (
                !NativeLibrary.TryGetExport(library, "SherpaOnnxGetOfflineStreamResult", out var get)
                || !NativeLibrary.TryGetExport(
                    library,
                    "SherpaOnnxDestroyOfflineRecognizerResult",
                    out var destroy
                )
            )
                return false;

            s_destroyResult = Marshal.GetDelegateForFunctionPointer<DestroyResult>(destroy);
            s_getResult = Marshal.GetDelegateForFunctionPointer<GetResult>(get);
            return true;
        }
    }
}
