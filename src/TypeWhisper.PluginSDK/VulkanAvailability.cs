using System.Runtime.InteropServices;

namespace TypeWhisper.PluginSDK;

/// <summary>Checks for a hardware Vulkan device without loading a model runtime.</summary>
/// <remarks>
///     <see cref="GetUnavailableReason" /> creates a Vulkan instance, which loads every installed
///     ICD in-process; call it only from the transcription worker, never from the host.
/// </remarks>
public static class VulkanAvailability
{
    /// <summary>Probes the Vulkan loader for an integrated, discrete or virtual GPU.</summary>
    /// <returns>Null when a GPU is available, otherwise a reason for CPU fallback.</returns>
    public static string? GetUnavailableReason()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "Vulkan acceleration is supported on Linux x64.";
        if (!NativeLibrary.TryLoad("libvulkan.so.1", out var library))
            return "The system Vulkan loader (libvulkan.so.1) is not installed.";

        try
        {
            var create = Marshal.GetDelegateForFunctionPointer<CreateInstance>(NativeLibrary.GetExport(library, "vkCreateInstance"));
            var destroy = Marshal.GetDelegateForFunctionPointer<DestroyInstance>(NativeLibrary.GetExport(library, "vkDestroyInstance"));
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumeratePhysicalDevices>(NativeLibrary.GetExport(library, "vkEnumeratePhysicalDevices"));
            var properties = Marshal.GetDelegateForFunctionPointer<GetPhysicalDeviceProperties>(NativeLibrary.GetExport(library, "vkGetPhysicalDeviceProperties"));
            // No extensions or layers, and a null applicationInfo requests Vulkan 1.0.
            var info = new InstanceCreateInfo { Type = 1 };
            var result = create(ref info, IntPtr.Zero, out var instance);
            if (result != 0)
                return $"No Vulkan GPU found (vkCreateInstance returned {result}).";
            try
            {
                // Retry VK_INCOMPLETE if devices change between the two calls.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    uint count = 0;
                    if (enumerate(instance, ref count, IntPtr.Zero) != 0 || count == 0)
                        return "No Vulkan GPU found.";
                    var capacity = count;
                    var devices = Marshal.AllocHGlobal(checked((int)capacity * IntPtr.Size));
                    try
                    {
                        result = enumerate(instance, ref count, devices);
                        if (result == 5) continue; // VK_INCOMPLETE
                        if (result != 0 || count > capacity)
                            return $"No Vulkan GPU found (device enumeration returned {result}).";
                        for (var i = 0; i < count; i++)
                        {
                            properties(Marshal.ReadIntPtr(devices, i * IntPtr.Size), out var device);
                            // Integrated, discrete, or virtual GPU. Exclude CPU (lavapipe)
                            // and OTHER, whose hardware acceleration is unspecified.
                            if (device.DeviceType is 1 or 2 or 3)
                                return null;
                        }
                        return "No Vulkan GPU found.";
                    }
                    finally { Marshal.FreeHGlobal(devices); }
                }
                return "No Vulkan GPU found.";
            }
            finally { destroy(instance, IntPtr.Zero); }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or OverflowException)
        {
            return $"Vulkan device probe failed: {ex.Message}";
        }
        finally { NativeLibrary.Free(library); }
    }

    /// <summary>
    ///     Cheap host-side check that a hardware Vulkan driver manifest is installed. It never
    ///     loads the loader or an ICD, so it is safe on the app's startup path; the real device
    ///     probe (<see cref="GetUnavailableReason" />) runs in the crash-isolated worker.
    /// </summary>
    public static bool HasHardwareDriverManifest() =>
        OperatingSystem.IsLinux() && HasHardwareDriverManifest(EnumerateDriverManifestPaths());

    internal static bool HasHardwareDriverManifest(IEnumerable<string> manifestPaths)
    {
        foreach (var path in manifestPaths)
        {
            List<string> files;
            try
            {
                // Materialize inside the try: enumeration is lazy and would otherwise throw at Any().
                files = Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*.json").ToList()
                    : File.Exists(path) ? [path] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // lavapipe (lvp) is Mesa's CPU rasterizer, which the device probe also rejects.
            if (files.Any(f => !Path.GetFileName(f).StartsWith("lvp_icd", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    // Mirrors the Vulkan loader's driver manifest search order.
    private static IEnumerable<string> EnumerateDriverManifestPaths()
    {
        var overrides = Environment.GetEnvironmentVariable("VK_DRIVER_FILES")
            ?? Environment.GetEnvironmentVariable("VK_ICD_FILENAMES");
        var additions = Environment.GetEnvironmentVariable("VK_ADD_DRIVER_FILES");
        foreach (var path in SplitPathList(additions))
            yield return path;
        if (!string.IsNullOrWhiteSpace(overrides))
        {
            foreach (var path in SplitPathList(overrides))
                yield return path;
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configHome
                ? configHome : Path.Join(home, ".config"),
        };
        roots.AddRange(SplitPathList(Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS") ?? "/etc/xdg"));
        roots.Add("/etc");
        roots.Add(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome : Path.Join(home, ".local", "share"));
        roots.AddRange(SplitPathList(Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share"));
        foreach (var root in roots)
            yield return Path.Join(root, "vulkan", "icd.d");
    }

    private static string[] SplitPathList(string? list) =>
        string.IsNullOrWhiteSpace(list) ? [] : list.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public uint Type;
        public IntPtr Next;
        public uint Flags;
        public IntPtr ApplicationInfo;
        public uint LayerCount;
        public IntPtr LayerNames;
        public uint ExtensionCount;
        public IntPtr ExtensionNames;
    }

    // Vulkan 1.0 VkPhysicalDeviceProperties is 824 bytes on Linux x64, including
    // VkPhysicalDeviceLimits and sparseProperties. Allocate the entire native
    // structure even though we only read deviceType at offset 16. Its ABI is fixed.
    // https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceProperties.html
    [StructLayout(LayoutKind.Explicit, Size = 824)]
    private struct PhysicalDeviceProperties
    {
        [FieldOffset(16)] public uint DeviceType;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateInstance(ref InstanceCreateInfo info, IntPtr allocator, out IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyInstance(IntPtr instance, IntPtr allocator);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetPhysicalDeviceProperties(IntPtr device, out PhysicalDeviceProperties properties);
}
