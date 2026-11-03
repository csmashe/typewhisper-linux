namespace TypeWhisper.Linux.Services.Telemetry;

public static class TelemetryNames
{
    public static string SanitizeModelId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "unknown";
        }

        id = id.Trim();
        if (id.Contains('/') || id.Contains('\\') || id.StartsWith('.') || id.StartsWith('~'))
        {
            return "custom";
        }

        return id[..Math.Min(id.Length, 120)];
    }

    /// <summary>Reduces a step name to [a-z0-9_] for use inside a measurement name.</summary>
    public static string MeasurementSegment(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        var segment = builder.ToString().Trim('_');
        return segment.Length == 0 ? "step" : segment;
    }
}
