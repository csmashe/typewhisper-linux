namespace TypeWhisper.Plugin.Script;

internal static class ScriptShells
{
    /// <summary>Whether <paramref name="name" /> resolves to an executable file on PATH.</summary>
    public static bool IsInstalled(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Join(directory, name);
                if (File.Exists(candidate) && HasExecuteBit(candidate))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or NotSupportedException
                                       or PlatformNotSupportedException
                                       or UnauthorizedAccessException)
            {
                // An unreadable PATH entry does not hide a shell installed elsewhere.
            }
        }

        return false;
    }

    private static bool HasExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;

        const UnixFileMode executable = UnixFileMode.UserExecute
                                        | UnixFileMode.GroupExecute
                                        | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & executable) != 0;
    }
}
