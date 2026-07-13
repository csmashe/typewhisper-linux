using System.Globalization;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Manages custom storage for large local model assets.
/// </summary>
public sealed class LocalModelStorageService
{
    // Large on-disk assets each local-model plugin keeps under its PluginData folder.
    // Small per-plugin settings.json stays in AppData and is intentionally not migrated,
    // so an unplugged custom-storage drive can never lose plugin configuration.
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> s_pluginAssetEntries =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["com.typewhisper.whisper-cpp"] = ["Models", "Runtimes"],
            ["com.typewhisper.sherpa-onnx"] = ["Models", "Runtimes"],
            ["com.typewhisper.gemma-local"] = ["Models"],
            ["com.typewhisper.supertonic-tts"] = ["Models"],
            ["com.typewhisper.granite-speech"] =
            [
                "python",
                "hf-cache",
                ".setup-complete",
                "python-embed.zip",
                "get-pip.py"
            ]
        };

    private readonly ISettingsService _settings;
    private readonly Action? _unloadActiveModels;
    // ReSharper disable once ReplaceWithFieldKeyword -- assigned in the constructor, where the `field` keyword is inaccessible.
    private readonly string _defaultModelStoragePath;
    private readonly string _defaultPluginDataPath;

    public LocalModelStorageService(
        ISettingsService settings,
        Action? unloadActiveModels = null,
        string? defaultModelStoragePath = null,
        string? defaultPluginDataPath = null)
    {
        _settings = settings;
        _unloadActiveModels = unloadActiveModels;
        _defaultModelStoragePath = defaultModelStoragePath ?? TypeWhisperEnvironment.ModelsPath;
        _defaultPluginDataPath = defaultPluginDataPath ?? TypeWhisperEnvironment.PluginDataPath;
    }

    public string ResolvedModelStoragePath =>
        LocalModelStoragePaths.ResolveModelStoragePath(_settings.Current, _defaultModelStoragePath);

    /// <summary>
    /// Resolves and validates the active local model storage path.
    /// </summary>
    private static void ResolveAvailableModelStoragePath(AppSettings settings)
    {
        var root = LocalModelStoragePaths.ResolveModelStoragePath(settings);
        if (AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is null)
        {
            Directory.CreateDirectory(root);
            return;
        }

        EnsureExistingWritableCustomRoot(root);
    }

    /// <summary>
    /// Resolves and validates the active plugin asset directory.
    /// </summary>
    public static string ResolveAvailablePluginAssetDirectory(
        AppSettings? settings,
        string pluginId,
        string? defaultPluginDataPath = null
    )
    {
        var directory =
            LocalModelStoragePaths.ResolvePluginAssetDirectory(settings, pluginId, defaultPluginDataPath);
        if (settings is null
            || AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is null)
        {
            Directory.CreateDirectory(directory);
            return directory;
        }

        ResolveAvailableModelStoragePath(settings);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Moves known large local model assets to the target path and saves it as the active storage path.
    /// </summary>
    public async Task MoveDownloadsAndUsePathAsync(string targetPath, CancellationToken ct = default)
    {
        var targetRoot = PrepareWritableTarget(targetPath);
        var sourceRoot = ResolvedModelStoragePath;
        var currentIsDefault =
            AppSettings.NormalizeLocalModelStoragePath(_settings.Current.LocalModelStoragePath) is null;
        var pluginAssetSourceRoot = currentIsDefault
            ? _defaultPluginDataPath
            : Path.Join(sourceRoot, LocalModelStoragePaths.PluginDataFolderName);

        if (PathsEqual(sourceRoot, targetRoot))
        {
            // Even when the target is the current default models root, the sibling default
            // plugin data must still move under <target>/PluginData — saving the setting alone
            // would strand it. Once already custom, the asset roots coincide; nothing to move.
            if (currentIsDefault)
            {
                _unloadActiveModels?.Invoke();
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    MigratePluginAssets(pluginAssetSourceRoot, targetRoot, ct);
                }, ct);
            }

            _settings.Save(_settings.Current with { LocalModelStoragePath = targetRoot });
            return;
        }

        if (IsNestedUnder(targetRoot, pluginAssetSourceRoot))
        {
            throw new LocalModelStorageUnavailableException(
                LocalModelStorageUnavailableReason.NestedUnderCurrentFolder,
                targetRoot,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Target model storage folder '{0}' must not be inside the current plugin asset folder '{1}'.",
                    targetRoot,
                    pluginAssetSourceRoot),
                currentPath: pluginAssetSourceRoot);
        }

        // A target nested under the source would make MigrateModelRootContents copy the
        // source's contents into one of its own subdirectories — self-recursive copying.
        if (IsNestedUnder(targetRoot, sourceRoot))
        {
            throw new LocalModelStorageUnavailableException(
                LocalModelStorageUnavailableReason.NestedUnderCurrentFolder,
                targetRoot,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Target model storage folder '{0}' must not be inside the current folder '{1}'.",
                    targetRoot,
                    sourceRoot),
                currentPath: sourceRoot);
        }

        _unloadActiveModels?.Invoke();

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            MigrateModelRootContents(sourceRoot, targetRoot, ct);
            MigratePluginAssets(pluginAssetSourceRoot, targetRoot, ct);
        }, ct);

        _settings.Save(_settings.Current with { LocalModelStoragePath = targetRoot });
    }

    /// <summary>
    /// Resets local model storage to the default app data path.
    /// </summary>
    public void ResetToDefault() =>
        _settings.Save(_settings.Current with { LocalModelStoragePath = null });

    private static string PrepareWritableTarget(string targetPath)
    {
        var normalized = AppSettings.NormalizeLocalModelStoragePath(targetPath)
            ?? throw new ArgumentException("A model storage path is required.", nameof(targetPath));

        var fullPath = Path.GetFullPath(normalized);
        Directory.CreateDirectory(fullPath);

        EnsureWritable(fullPath);

        return fullPath;
    }

    private static void EnsureExistingWritableCustomRoot(string fullPath)
    {
        if (!Directory.Exists(fullPath))
        {
            throw new LocalModelStorageUnavailableException(
                LocalModelStorageUnavailableReason.DoesNotExist,
                fullPath,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Model storage folder does not exist: {0}",
                    fullPath));
        }

        EnsureWritable(fullPath);
    }

    private static void EnsureWritable(string fullPath)
    {
        var probePath = Path.Join(fullPath, $".typewhisper-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "");
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LocalModelStorageUnavailableException(
                LocalModelStorageUnavailableReason.NotWritable,
                fullPath,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Model storage folder is not writable: {0}",
                    fullPath),
                innerException: ex);
        }
    }

    private static void MigrateModelRootContents(string sourceRoot, string targetRoot, CancellationToken ct)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        Directory.CreateDirectory(targetRoot);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (string.Equals(name, LocalModelStoragePaths.PluginDataFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            MoveEntry(entry, Path.Join(targetRoot, SafeLeafName(name, nameof(entry))));
        }
    }

    private static void MigratePluginAssets(string assetSourceRoot, string targetRoot, CancellationToken ct)
    {
        var pluginDataFolderName = SafeRelativeName(LocalModelStoragePaths.PluginDataFolderName, nameof(LocalModelStoragePaths.PluginDataFolderName));

        foreach (var (pluginId, entries) in s_pluginAssetEntries)
        {
            ct.ThrowIfCancellationRequested();
            var pluginFolderName = SafeLeafName(pluginId, nameof(pluginId));
            var sourcePluginDir = Path.Join(assetSourceRoot, pluginFolderName);
            if (!Directory.Exists(sourcePluginDir))
                continue;

            var targetPluginDir = Path.Join(targetRoot, pluginDataFolderName, pluginFolderName);
            foreach (var entryName in entries)
            {
                ct.ThrowIfCancellationRequested();
                var safeEntryName = SafeRelativeName(entryName, nameof(entryName));
                var sourceEntry = Path.Join(sourcePluginDir, safeEntryName);
                var targetEntry = Path.Join(targetPluginDir, safeEntryName);
                MoveEntry(sourceEntry, targetEntry);
            }
        }
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
                MoveEntry(child, Path.Join(target, SafeLeafName(Path.GetFileName(child), nameof(child))));

            TryDeleteDirectoryIfEmpty(source);
            return;
        }

        if (!File.Exists(source) || File.Exists(target))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        File.Delete(source);
    }

    private static string SafeLeafName(string value, string parameterName)
    {
        var safeName = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            throw new InvalidOperationException($"Invalid path segment for {parameterName}.");

        return safeName;
    }

    private static string SafeRelativeName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Invalid relative path segment for {parameterName}.");
        }

        return value;
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not delete empty model storage directory '{0}': {1}",
                path,
                ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not delete empty model storage directory '{0}': {1}",
                path,
                ex.Message);
        }
    }

    // Linux filesystems are case-sensitive, so compare paths ordinally (not OrdinalIgnoreCase
    // as on Windows) — otherwise distinct directories like Models/ and models/ would be
    // treated as the same root and migration would be skipped.
    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.Ordinal);

    // True when child is strictly nested under ancestor. Ordinal comparison matches
    // PathsEqual: Linux filesystems are case-sensitive, so distinct casings are
    // distinct paths.
    private static bool IsNestedUnder(string child, string ancestor)
    {
        var ancestorFull = Path.GetFullPath(ancestor)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childFull = Path.GetFullPath(child)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return childFull.Length > ancestorFull.Length
            && childFull.StartsWith(ancestorFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
