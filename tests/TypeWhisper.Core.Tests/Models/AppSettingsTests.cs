using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

/// <summary>Covers <see cref="AppSettings" /> defaults and its normalize/clamp helpers (auto-hide, acceleration, overlay position).</summary>
public class AppSettingsTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void LegacyHotkeyProperties_AreIgnoredOnLoadAndOmittedOnSave()
    {
        const string legacyJson =
            """
            {
              "pushToTalkHotkey": "Ctrl+Shift+F8",
              "toggleOnlyHotkey": "Ctrl+Shift+F9",
              "holdOnlyHotkey": "Ctrl+Shift+F10",
              "language": "de",
              "autoPaste": false
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson, s_jsonOptions);

        Assert.NotNull(settings);
        Assert.Equal("de", settings.Language);
        Assert.False(settings.AutoPaste);

        var reserializedJson = JsonSerializer.Serialize(settings, s_jsonOptions);
        using var reserialized = JsonDocument.Parse(reserializedJson);
        var root = reserialized.RootElement;

        Assert.False(root.TryGetProperty("pushToTalkHotkey", out _));
        Assert.False(root.TryGetProperty("toggleOnlyHotkey", out _));
        Assert.False(root.TryGetProperty("holdOnlyHotkey", out _));
    }

    [Fact]
    public void DefaultPreviewBubbleAutoHideMilliseconds_IsFifteenHundred()
    {
        Assert.Equal(1500, AppSettings.DefaultPreviewBubbleAutoHideMilliseconds);
        Assert.Equal(1500, AppSettings.Default.PreviewBubbleAutoHideMilliseconds);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1500, 1500)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 5000)]
    public void NormalizePreviewBubbleAutoHideMilliseconds_ClampsToSupportedRange(
        int input,
        int expected)
    {
        Assert.Equal(expected, AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(input));
    }

    [Fact]
    public void DefaultSelectedIndustryPresetId_IsGeneral()
    {
        Assert.Equal("general", AppSettings.Default.SelectedIndustryPresetId);
    }

    [Fact]
    public void DefaultTranscriptionNumberNormalizationEnabled_IsTrue()
    {
        Assert.True(AppSettings.Default.TranscriptionNumberNormalizationEnabled);
    }

    [Fact]
    public void DefaultLocalModelAcceleration_IsAuto()
    {
        Assert.Equal(
            AppSettings.LocalModelAccelerationAuto,
            AppSettings.Default.LocalModelAcceleration);
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("   ", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("cpu", "cpu")]
    [InlineData("CPU", "cpu")]
    [InlineData("nvidia-cuda", "nvidia-cuda")]
    [InlineData("NVIDIA-CUDA", "nvidia-cuda")]
    [InlineData("NVIDIA CUDA", "nvidia-cuda")]
    [InlineData("cuda", "nvidia-cuda")]
    [InlineData("directml", "auto")]
    [InlineData("anything-else", "auto")]
    public void NormalizeLocalModelAcceleration_NormalizesKnownValuesAndFallsBackToAuto(
        string? input,
        string expected)
    {
        Assert.Equal(expected, AppSettings.NormalizeLocalModelAcceleration(input));
    }

    [Fact]
    public void DefaultOverlayCustomPosition_IsNull()
    {
        Assert.Null(AppSettings.Default.OverlayCustomLeft);
        Assert.Null(AppSettings.Default.OverlayCustomTop);
    }

    [Theory]
    // Inside work area → passthrough.
    [InlineData(100, 50, 0, 0, 1920, 1080, 320, 80, 100, 50)]
    // Past the right/bottom edge → clamped to right/bottom minus size.
    [InlineData(2000, 1200, 0, 0, 1920, 1080, 320, 80, 1600, 1000)]
    // Past the left/top edge → clamped to the work area origin.
    [InlineData(-50, -25, 0, 0, 1920, 1080, 320, 80, 0, 0)]
    // Multi-monitor: non-zero work-area origin.
    [InlineData(50, 30, 1920, 0, 3840, 1080, 320, 80, 1920, 30)]
    // Degenerate: window larger than the work area → clamps to the min, not negative.
    [InlineData(500, 500, 0, 0, 200, 100, 400, 200, 0, 0)]
    public void ClampOverlayPositionToWorkArea_ClampsToBounds(
        double left,
        double top,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom,
        double windowWidth,
        double windowHeight,
        double expectedLeft,
        double expectedTop)
    {
        var (clampedLeft, clampedTop) = AppSettings.ClampOverlayPositionToWorkArea(
            left,
            top,
            workAreaLeft,
            workAreaTop,
            workAreaRight,
            workAreaBottom,
            windowWidth,
            windowHeight);

        Assert.Equal(expectedLeft, clampedLeft);
        Assert.Equal(expectedTop, clampedTop);
    }
}
