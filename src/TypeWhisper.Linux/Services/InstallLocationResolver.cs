namespace TypeWhisper.Linux.Services;

/// <summary>Reports the AppImage file when launched from one; the executable itself sits in a temporary mount.</summary>
internal static class InstallLocationResolver
{
    internal static string Resolve() => Resolve(
        Environment.GetEnvironmentVariable("APPIMAGE"), Environment.ProcessPath, AppContext.BaseDirectory);

    internal static string Resolve(string? appImagePath, string? processPath, string baseDirectory)
    {
        var location = !string.IsNullOrWhiteSpace(appImagePath) && Path.IsPathRooted(appImagePath)
            ? appImagePath
            : Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(location))
        {
            location = baseDirectory;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
    }
}
