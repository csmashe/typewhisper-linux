namespace TypeWhisper.Plugin.AuthenticatedCli;

/// <summary>
///     Finds provider CLIs by name on the process <c>PATH</c>. Symlinks are the normal shape of
///     an npm-global or <c>~/.local/bin</c> install, so they are followed rather than rejected;
///     what must hold is that the final target is a regular, executable file.
/// </summary>
internal sealed class CliExecutableDiscovery
{
    private readonly Func<string?> _readPath;

    internal CliExecutableDiscovery()
        : this(() => Environment.GetEnvironmentVariable("PATH"))
    {
    }

    internal CliExecutableDiscovery(Func<string?> readPath)
    {
        _readPath = readPath;
    }

    internal IReadOnlyList<string> FindCandidates(string executableName)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var value = _readPath();
        if (string.IsNullOrWhiteSpace(value))
        {
            return candidates;
        }

        foreach (var rawDirectory in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim();
            if (!CliPathSafety.IsSafeLocalDirectory(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Join(directory, executableName));
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException)
            {
                continue;
            }

            if (!IsUsableExecutable(candidate, executableName) || !seen.Add(candidate))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>
    ///     Whether the path is a candidate this plugin will launch: the expected file name, and a
    ///     final link target that is an existing file with an execute bit. A shell script counts —
    ///     npm-global installs of these CLIs commonly are one.
    /// </summary>
    internal static bool IsUsableExecutable(string path, string executableName) =>
        ResolveRealPath(path, executableName) is not null;

    /// <summary>
    ///     Returns the final link target of a usable candidate, or null when the path is not a
    ///     regular executable file named <paramref name="executableName" />. The resolved path is
    ///     kept for diagnostics and for verifying the binary has not been swapped before launch.
    /// </summary>
    internal static string? ResolveRealPath(string path, string executableName)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(fullPath), executableName, StringComparison.Ordinal)
                || !File.Exists(fullPath))
            {
                return null;
            }

            var resolved = File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName
                           ?? fullPath;
            if (!File.Exists(resolved) || Directory.Exists(resolved))
            {
                return null;
            }

            return HasExecuteBit(resolved) ? resolved : null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or PlatformNotSupportedException
                                   or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasExecuteBit(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode executable = UnixFileMode.UserExecute
                                        | UnixFileMode.GroupExecute
                                        | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & executable) != 0;
    }
}

internal static class CliPathSafety
{
    internal static bool IsSafeLocalDirectory(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && Directory.Exists(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
