namespace TypeWhisper.Linux.Services.UserData;

/// <summary>What kind of data an entry in the TypeWhisper data folder holds.</summary>
public enum LocalDataCategory
{
    /// <summary>App settings and Linux preferences; stored secrets are removed on export.</summary>
    Settings,

    /// <summary>History, dictionary, profiles, snippets, prompt actions, statistics and the error log.</summary>
    AppData,

    /// <summary>Each plugin's settings and collections (the files directly in its folder).</summary>
    PluginData,

    /// <summary>Dictation recovery captures and Recorder recordings.</summary>
    Recordings,

    /// <summary>Application log files.</summary>
    Logs,

    /// <summary>Copies of GNOME keybindings taken before TypeWhisper changed them.</summary>
    ShortcutBackups,

    /// <summary>The key that encrypts stored secrets and retired provider secrets.</summary>
    Secrets,

    /// <summary>Downloaded models, including those inside plugin folders.</summary>
    Models,

    /// <summary>Downloaded GPU runtimes and other native libraries.</summary>
    Runtimes,

    /// <summary>Installed plugin packages.</summary>
    InstalledPlugins,

    /// <summary>The per-user copy of the command line tool.</summary>
    CommandLineTool,

    /// <summary>Sockets, locks, restore staging and other files TypeWhisper only uses while running.</summary>
    Internal,

    /// <summary>Not created by TypeWhisper.</summary>
    Unrecognized,
}

/// <summary>
///     The documented contents of the TypeWhisper data folder: which top-level entry holds which
///     <see cref="LocalDataCategory" />, and which of those an export of all data carries.
/// </summary>
public static class LocalDataInventory
{
    private static readonly Dictionary<string, LocalDataCategory> s_topLevelFiles = new(
        StringComparer.Ordinal
    )
    {
        ["settings.json"] = LocalDataCategory.Settings,
        ["settings.json.bak"] = LocalDataCategory.Settings,
        ["linux-preferences.json"] = LocalDataCategory.Settings,
        ["secret-protection.key"] = LocalDataCategory.Secrets,
        ["retired-provider-secrets.quarantine.json"] = LocalDataCategory.Secrets,
    };

    private static readonly Dictionary<string, LocalDataCategory> s_topLevelFolders = new(
        StringComparer.Ordinal
    )
    {
        ["Data"] = LocalDataCategory.AppData,
        ["PluginData"] = LocalDataCategory.PluginData,
        ["Audio"] = LocalDataCategory.Recordings,
        ["Logs"] = LocalDataCategory.Logs,
        ["backups"] = LocalDataCategory.ShortcutBackups,
        ["Models"] = LocalDataCategory.Models,
        ["Runtimes"] = LocalDataCategory.Runtimes,
        ["Plugins"] = LocalDataCategory.InstalledPlugins,
        ["Cli"] = LocalDataCategory.CommandLineTool,
        ["Runtime"] = LocalDataCategory.Internal,
        ["ManagedArtifacts"] = LocalDataCategory.Internal,
    };

    /// <summary>Categories an export always carries.</summary>
    public static IReadOnlyList<LocalDataCategory> AlwaysExported { get; } =
    [
        LocalDataCategory.Settings,
        LocalDataCategory.AppData,
        LocalDataCategory.PluginData,
        LocalDataCategory.Logs,
        LocalDataCategory.ShortcutBackups,
    ];

    /// <summary>Classifies a top-level entry of the data folder by name.</summary>
    public static LocalDataCategory ClassifyTopLevel(string name, bool isDirectory)
    {
        // Restore staging, the restore lock and other dot entries are internal.
        if (name.StartsWith('.'))
        {
            return LocalDataCategory.Internal;
        }

        var known = isDirectory ? s_topLevelFolders : s_topLevelFiles;
        if (known.TryGetValue(name, out var category))
        {
            return category;
        }

        // Copies a store keeps beside its file, such as a corrupt file set aside (.broken-*).
        return !isDirectory && s_topLevelFiles.Keys.Any(file => name.StartsWith(file + ".", StringComparison.Ordinal))
            ? LocalDataCategory.Internal
            : LocalDataCategory.Unrecognized;
    }

    /// <summary>
    ///     Whether a file below a category folder is exported. <paramref name="segments" /> is the
    ///     path relative to the data folder, starting with the category folder.
    /// </summary>
    public static bool IsExportedBelow(LocalDataCategory category, IReadOnlyList<string> segments)
    {
        var name = segments[^1];
        if (segments.Skip(1).Any(segment => segment.StartsWith('.')) || IsTransient(name))
        {
            return false;
        }

        // A plugin keeps settings and collections as files directly in its folder; its
        // subfolders hold models, runtimes, scratch space and locks.
        return category != LocalDataCategory.PluginData || segments.Count == 3;
    }

    /// <summary>Whether a folder below a category folder can hold exported files.</summary>
    public static bool IsFolderExportedBelow(LocalDataCategory category, IReadOnlyList<string> segments) =>
        !segments[^1].StartsWith('.')
        && (category != LocalDataCategory.PluginData || segments.Count == 2);

    private static bool IsTransient(string name) =>
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
}
