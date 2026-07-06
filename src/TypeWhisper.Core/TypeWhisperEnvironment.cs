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

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(ModelsPath);
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(AudioPath);
        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(PluginDataPath);
    }
}