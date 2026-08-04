namespace TypeWhisper.Linux.Services;

/// <summary>
///     Resolution of the XDG base directories the app writes user data into.
/// </summary>
internal static class XdgPaths
{
    /// <summary>
    ///     <c>$XDG_DATA_HOME</c>, falling back to <c>~/.local/share</c> when it is unset
    ///     or relative. The spec treats a relative value as invalid, and honouring one
    ///     would resolve writes against the CWD, where the session never looks.
    /// </summary>
    internal static string ResolveDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
        {
            return xdg;
        }

        // DoNotVerify: the default option returns an empty string for a HOME that is not
        // on disk, which would make this fallback relative and defeat the check above.
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify
        );
        return Path.Join(home, ".local", "share");
    }
}
