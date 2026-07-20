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
    ///     False means recordings there may be readable by other local users. Surfaced rather than
    ///     made fatal: refusing to run would strand users whose data directory is on a mount that
    ///     carries no Unix modes.
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

        // Recordings and their transcript sidecars are raw captures of the user's speech, so this
        // one is owner-only. Created at 0700 rather than created-then-chmodded so a fresh install
        // is never briefly group-readable. Creation failures stay fatal like the directories above.
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(AudioPath);
        }
        else
        {
            Directory.CreateDirectory(AudioPath, DirMode0700);
        }

        // Only the hardening degrades to a warning; it also tightens directories left at 0755 by
        // earlier versions. Files stay umask-governed by design (see AtomicFileWrite) — the
        // directory is the boundary that closes this.
        AudioDirectoryIsOwnerOnly = TryMakeOwnerOnly(AudioPath);
    }

    /// <summary>
    ///     Tightens a directory to 0700 and confirms it took, returning <c>false</c> when the
    ///     owner-only boundary could not be established. Never throws: a mount that ignores modes
    ///     must not stop startup, but it must not pass silently either.
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

            // Verify rather than trust: chmod is a silent no-op on filesystems carrying no Unix
            // modes (exFAT/NTFS), which is exactly when recordings stay readable to everyone.
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