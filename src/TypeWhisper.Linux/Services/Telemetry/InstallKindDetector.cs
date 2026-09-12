namespace TypeWhisper.Linux.Services.Telemetry;

public static class InstallKindDetector
{
    public static string Detect()
    {
        return Detect(Environment.GetEnvironmentVariable("APPIMAGE"), Environment.ProcessPath);
    }

    public static string Detect(string? appImageEnv, string? processPath)
    {
        return Path.IsPathRooted(appImageEnv) ? "appimage" :
            processPath is not null && (processPath.StartsWith("/opt/", StringComparison.Ordinal)
                || processPath.StartsWith("/usr/", StringComparison.Ordinal)) ? "system" : "local";
    }
}
