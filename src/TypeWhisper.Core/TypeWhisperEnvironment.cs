using System.Diagnostics;

namespace TypeWhisper.Core;

public static class TypeWhisperEnvironment
{
    public static string BasePath { get; } = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeWhisper"
    );

    public static string ModelsPath => Path.Join(BasePath, "Models");
    public static string DataPath => Path.Join(BasePath, "Data");
    public static string LogsPath => Path.Join(BasePath, "Logs");
    public static string PluginsPath => Path.Join(BasePath, "Plugins");
    public static string AudioPath => Path.Join(BasePath, "Audio");
    public static string PluginDataPath => Path.Join(BasePath, "PluginData");
    public static string SettingsFilePath => Path.Join(BasePath, "settings.json");

    private const UnixFileMode DirMode0700 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    ///     Whether <see cref="AudioPath" /> is owner-only after the last <see cref="EnsureDirectories" />.
    ///     False means recordings written there may be readable by other local users — the mount
    ///     ignored the mode (exFAT/NTFS) or the chmod failed. Surfaced at startup rather than made
    ///     fatal: refusing to run would strand users whose data directory lives on such a mount.
    /// </summary>
    public static bool AudioDirectoryIsOwnerOnly { get; private set; } = true;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(PluginDataPath);

        // Recordings and their transcript sidecars are raw captures of the user's speech, so the
        // directory is owner-only. Created at 0700 rather than created-then-chmodded so a fresh
        // install is never briefly group/other-readable. Creation failures stay fatal like every
        // other directory above: without this directory dictation and recording cannot work, so
        // continuing would only defer the same failure to the first save.
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(AudioPath);
        }
        else
        {
            Directory.CreateDirectory(AudioPath, DirMode0700);
        }

        // Only the hardening itself degrades to a warning — it covers directories left at 0755 by
        // earlier versions, and mounts that cannot carry the mode at all. Files stay umask-governed
        // by design (see AtomicFileWrite); the directory is the boundary that closes this.
        AudioDirectoryIsOwnerOnly = TryMakeOwnerOnly(AudioPath);
    }

    /// <summary>
    ///     Tightens an existing directory to 0700 and confirms it took. Returns <c>false</c> when
    ///     the owner-only boundary could not be established, so a caller can surface it. Never
    ///     throws: a mount that ignores modes must not stop the app from starting, but it must not
    ///     pass silently either.
    /// </summary>
    private static bool TryMakeOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            File.SetUnixFileMode(path, DirMode0700);

            // Verify rather than trust: the chmod can be a silent no-op on filesystems that do
            // not carry Unix modes (a mounted exFAT/NTFS recordings folder), and that is exactly
            // the case where the recordings stay readable to everyone.
            const UnixFileMode exposed =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & exposed) == 0)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[TypeWhisperEnvironment] Could not secure '{path}': {ex.Message}");
            return false;
        }

        Trace.WriteLine(
            $"[TypeWhisperEnvironment] '{path}' is not owner-only; recordings stored there may be "
            + "readable by other local users."
        );
        return false;
    }
}