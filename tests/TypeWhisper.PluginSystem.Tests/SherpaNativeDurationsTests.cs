extern alias SherpaOnnx;

using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaNativeDurationsTests
{
    [Fact]
    public void Verify_MatchingTimestamps_ReturnsDurations()
    {
        float[] timestamps = [1.28f, 1.6f, 1.92f];
        float[] durations = [0.32f, 0.24f, 0.16f];

        Assert.Same(durations, SherpaNativeDurations.Verify(timestamps, [.. timestamps], durations));
    }

    [Theory]
    // A different timestamp means the offsets do not describe this native build.
    [InlineData(new[] { 1.28f, 1.6f, 1.93f }, new[] { 0.32f, 0.24f, 0.16f })]
    [InlineData(new[] { 1.28f, 1.6f }, new[] { 0.32f, 0.24f, 0.16f })]
    [InlineData(new[] { 1.28f, 1.6f, 1.92f }, new[] { 0.32f, 0.24f })]
    [InlineData(new[] { 1.28f, 1.6f, 1.92f }, new[] { 0.32f, float.NaN, 0.16f })]
    [InlineData(new[] { 1.28f, 1.6f, 1.92f }, new[] { 0.32f, -4.5f, 0.16f })]
    [InlineData(new[] { 1.28f, 1.6f, 1.92f }, new[] { 0.32f, float.PositiveInfinity, 0.16f })]
    public void Verify_MismatchedOrInvalidNativeValues_ReturnsNull(float[] native, float[] durations)
    {
        Assert.Null(SherpaNativeDurations.Verify([1.28f, 1.6f, 1.92f], native, durations));
    }

    [Fact]
    public void SelectLoadedCApi_PrefersTheResolversCopy()
    {
        var lookedUp = false;

        var selected = SherpaOnnxNativeRuntime.SelectLoadedCApi(
            new IntPtr(0x1000),
            () =>
            {
                lookedUp = true;
                return new IntPtr(0x2000);
            }
        );

        Assert.Equal(new IntPtr(0x1000), selected);
        Assert.False(lookedUp);
    }

    [Fact]
    public void SelectLoadedCApi_WithoutAResolverCopy_UsesTheLoadedOne()
    {
        Assert.Equal(
            new IntPtr(0x2000),
            SherpaOnnxNativeRuntime.SelectLoadedCApi(IntPtr.Zero, () => new IntPtr(0x2000))
        );
    }

    // Guards the binding defect the reader works around. If an update fixes the managed
    // layout, this fails so the workaround can be removed.
    [Fact]
    public void ManagedResultLayout_StillReadsDurationsAtTheTokensArrayOffset()
    {
        var impl = typeof(SherpaOnnx.OfflineRecognizerResult).GetNestedType(
            "Impl",
            BindingFlags.NonPublic
        );

        Assert.NotNull(impl);
        Assert.Equal(32, (int)Marshal.OffsetOf(impl, "Durations"));
    }

    [Fact]
    public void OfflineStream_ExposesTheNativeHandleTheReaderUses()
    {
        var field = typeof(SherpaOnnx.OfflineStream).GetField(
            "_handle",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        Assert.Equal(typeof(HandleRef), field?.FieldType);
    }
}
