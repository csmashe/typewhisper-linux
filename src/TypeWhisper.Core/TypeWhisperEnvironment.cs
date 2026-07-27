using System.Diagnostics;

namespace TypeWhisper.Core;

public static class TypeWhisperEnvironment
{
    private const UnixFileMode DirMode0700 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

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

    public static void EnsureDirectories()
    {
        // CreateDirectory honors the umask (0002 leaves this group-writable), and write access
        // to the parent governs renaming -- a loose BasePath lets a peer swap out a child.
        Directory.CreateDirectory(BasePath);
        EnsureDirectoryMode0700(BasePath);

        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(AudioPath);
        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(PluginDataPath);

        // Defense in depth: recordings stay owner-only even if BasePath is loosened.
        EnsureDirectoryMode0700(AudioPath);
    }

    private static void EnsureDirectoryMode0700(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, DirMode0700);
        }
        catch (Exception ex)
        {
            // Not fatal by itself -- the mode check below decides. A mount can reject chmod
            // while already presenting an owner-only mode.
            Trace.WriteLine(
                $"[TypeWhisperEnvironment] Could not set 0700 mode on '{path}': {ex.Message}"
            );
        }

        // Verify rather than trust the call: FAT and some CIFS mounts accept chmod and ignore
        // it. Startup must not continue with recordings and settings readable by other accounts,
        // so fail closed instead of returning as if the directory had been tightened.
        const UnixFileMode forbidden =
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;

        var mode = File.GetUnixFileMode(path);
        if ((mode & forbidden) != 0)
        {
            throw new IOException(
                $"'{path}' must be owner-only (0700) but is {mode}. Fix it with: chmod 700 '{path}'"
            );
        }
    }
}