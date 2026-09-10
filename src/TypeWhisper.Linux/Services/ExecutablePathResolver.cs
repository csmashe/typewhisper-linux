namespace TypeWhisper.Linux.Services;

/// <summary>
///     Resolves a command name against PATH the way <c>execvp</c> does: directories in order,
///     first entry that exists and carries an execute bit wins.
/// </summary>
internal static class ExecutablePathResolver
{
    private const UnixFileMode ExecutableModeMask =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    internal static string? Find(string commandName)
    {
        return Find(commandName, Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>Returns the absolute path of the resolved executable, or null if there is none.</summary>
    internal static string? Find(string commandName, string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        // Bare names only, like execvp: a separator would let Path.Join escape the PATH directory.
        if (
            commandName.Contains(Path.DirectorySeparatorChar)
            || commandName.Contains(Path.AltDirectorySeparatorChar)
        )
        {
            return null;
        }

        foreach (
            var directory in pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Join(directory, commandName));
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
                if (
                    File.Exists(candidate)
                    && (File.GetUnixFileMode(candidate) & ExecutableModeMask) != 0
                )
#pragma warning restore CA1416
                {
                    return candidate;
                }
            }
            catch
            {
                // Invalid, inaccessible, or stale PATH entry: continue discovery.
            }
        }

        return null;
    }
}
