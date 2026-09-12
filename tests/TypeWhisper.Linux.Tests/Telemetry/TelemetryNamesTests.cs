using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

public sealed class TelemetryNamesTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("parakeet-tdt-0.6b-v3", "parakeet-tdt-0.6b-v3")]
    [InlineData("/home/u/models/x.bin", "custom")]
    [InlineData("~/x", "custom")]
    [InlineData("./x", "custom")]
    [InlineData(@"C:\models\x.bin", "custom")]
    [InlineData(" model ", "model")]
    public void Sanitizes_model_id(string? id, string expected)
    {
        Assert.Equal(expected, TelemetryNames.SanitizeModelId(id));
    }

    [Fact]
    public void Limits_model_length()
    {
        Assert.Equal(new string('a', 120), TelemetryNames.SanitizeModelId(new string('a', 200)));
    }

    [Theory]
    [InlineData("LLM", "llm")]
    [InlineData("Plugin(49)", "plugin_49")]
    [InlineData("Spoken Formatting", "spoken_formatting")]
    [InlineData("(((", "step")]
    public void Builds_measurement_segments(string name, string expected) =>
        Assert.Equal(expected, TelemetryNames.MeasurementSegment(name));
}
