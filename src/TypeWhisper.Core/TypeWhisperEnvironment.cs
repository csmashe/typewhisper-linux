namespace TypeWhisper.Core;

public static class TypeWhisperEnvironment
{
    public const string GithubRepoUrl = "https://github.com/csmashe/typewhisper-linux";

    public static string BasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeWhisper"
    );

    public static string ModelsPath => Path.Combine(BasePath, "Models");
    public static string DataPath => Path.Combine(BasePath, "Data");
    public static string LogsPath => Path.Combine(BasePath, "Logs");
    public static string PluginsPath => Path.Combine(BasePath, "Plugins");
    public static string AudioPath => Path.Combine(BasePath, "Audio");
    public static string PluginDataPath => Path.Combine(BasePath, "PluginData");
    public static string SettingsFilePath => Path.Combine(BasePath, "settings.json");
    public static string DatabasePath => Path.Combine(DataPath, "typewhisper.db");

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