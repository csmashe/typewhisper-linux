using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class VulkanAvailabilityTests
{
    private sealed class TempAssetDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Join(System.IO.Path.GetTempPath(), "tw-vk-" + Guid.NewGuid().ToString("N"));

        public TempAssetDir() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }

    [Fact]
    public void HardwareDriverManifest_MissingDirectory_IsFalse()
    {
        using var temp = new TempAssetDir();
        Assert.False(VulkanAvailability.HasHardwareDriverManifest([Path.Join(temp.Path, "icd.d")]));
    }

    [Fact]
    public void HardwareDriverManifest_LavapipeOnly_IsFalse()
    {
        using var temp = new TempAssetDir();
        File.WriteAllText(Path.Join(temp.Path, "lvp_icd.x86_64.json"), "{}");
        File.WriteAllText(Path.Join(temp.Path, "notes.txt"), "ignored");
        Assert.False(VulkanAvailability.HasHardwareDriverManifest([temp.Path]));
    }

    [Fact]
    public void HardwareDriverManifest_VendorIcd_IsTrue()
    {
        using var temp = new TempAssetDir();
        File.WriteAllText(Path.Join(temp.Path, "lvp_icd.x86_64.json"), "{}");
        File.WriteAllText(Path.Join(temp.Path, "nvidia_icd.x86_64.json"), "{}");
        Assert.True(VulkanAvailability.HasHardwareDriverManifest([Path.Join(temp.Path, "missing"), temp.Path]));
    }

    [Fact]
    public void HardwareDriverManifest_ExplicitFile_IsTrue()
    {
        using var temp = new TempAssetDir();
        var manifest = Path.Join(temp.Path, "radeon_icd.x86_64.json");
        File.WriteAllText(manifest, "{}");
        Assert.True(VulkanAvailability.HasHardwareDriverManifest([manifest]));
    }
}
