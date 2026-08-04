using System.Diagnostics;
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
                "get-pip.py",
            ],
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
        var migrated = new MigratedTargets();
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
                    CopyPluginAssets(pluginAssetSourceRoot, targetRoot, migrated, ct);
                }, ct);
            }

            _settings.Save(_settings.Current with { LocalModelStoragePath = targetRoot });

            if (currentIsDefault)
            {
                // Settings already point at targetRoot, so this cleanup is best-effort: a failure or
                // interruption wastes disk space, never data — hence CancellationToken.None after the commit.
                await Task.Run(
                    () => TryCleanUp(() =>
                        DeletePluginAssetSourceContents(pluginAssetSourceRoot, targetRoot, migrated)),
                    CancellationToken.None);
            }

            return;
        }

        // Equality as well as nesting: the plugin-asset root is a sibling of the default models
        // root, so selecting it verbatim slips past the source-equality and models-nesting guards
        // and would migrate assets into a degenerate PluginData/PluginData tree.
        if (PathsEqual(targetRoot, pluginAssetSourceRoot)
            || IsNestedUnder(targetRoot, pluginAssetSourceRoot))
        {
            throw new LocalModelStorageUnavailableException(
                LocalModelStorageUnavailableReason.NestedUnderCurrentFolder,
                targetRoot,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Target model storage folder '{0}' must not be, or be inside, the current plugin asset folder '{1}'.",
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
            CopyModelRootContents(sourceRoot, targetRoot, migrated, ct);
            CopyPluginAssets(pluginAssetSourceRoot, targetRoot, migrated, ct);
        }, ct);

        _settings.Save(_settings.Current with { LocalModelStoragePath = targetRoot });

        // Best-effort cleanup after the commit above — see comment in the currentIsDefault branch.
        await Task.Run(() =>
        {
            TryCleanUp(() => DeleteModelRootSourceContents(sourceRoot, targetRoot, migrated));
            TryCleanUp(() =>
                DeletePluginAssetSourceContents(pluginAssetSourceRoot, targetRoot, migrated));
        }, CancellationToken.None);
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

    private static void CopyModelRootContents(
        string sourceRoot,
        string targetRoot,
        MigratedTargets migrated,
        CancellationToken ct)
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

            CopyEntry(entry, Path.Join(targetRoot, SafeLeafName(name, nameof(entry))), migrated, ct);
        }
    }

    private static void DeleteModelRootSourceContents(
        string sourceRoot,
        string targetRoot,
        MigratedTargets migrated)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, LocalModelStoragePaths.PluginDataFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            DeleteMigratedEntry(entry, Path.Join(targetRoot, SafeLeafName(name, nameof(entry))), migrated);
        }
    }

    private static void CopyPluginAssets(
        string assetSourceRoot,
        string targetRoot,
        MigratedTargets migrated,
        CancellationToken ct)
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
                CopyEntry(
                    Path.Join(sourcePluginDir, safeEntryName),
                    Path.Join(targetPluginDir, safeEntryName),
                    migrated,
                    ct);
            }
        }
    }

    private static void DeletePluginAssetSourceContents(
        string assetSourceRoot,
        string targetRoot,
        MigratedTargets migrated)
    {
        var pluginDataFolderName = SafeRelativeName(LocalModelStoragePaths.PluginDataFolderName, nameof(LocalModelStoragePaths.PluginDataFolderName));

        foreach (var (pluginId, entries) in s_pluginAssetEntries)
        {
            var pluginFolderName = SafeLeafName(pluginId, nameof(pluginId));
            var sourcePluginDir = Path.Join(assetSourceRoot, pluginFolderName);
            if (!Directory.Exists(sourcePluginDir))
                continue;

            var targetPluginDir = Path.Join(targetRoot, pluginDataFolderName, pluginFolderName);
            foreach (var entryName in entries)
            {
                var safeEntryName = SafeRelativeName(entryName, nameof(entryName));
                DeleteMigratedEntry(
                    Path.Join(sourcePluginDir, safeEntryName),
                    Path.Join(targetPluginDir, safeEntryName),
                    migrated);
            }
        }
    }

    /// <summary>
    ///     The targets this run copied itself, stamped with the size and modification time they had
    ///     immediately after the copy. Deletion is gated on this rather than on any inference from
    ///     the target's contents — equal size is not proof of equal bytes, and a wrong guess costs
    ///     the user the only remaining copy of a multi-gigabyte model. Hashing instead would roughly
    ///     double migration I/O and still be a time-of-check race; a replacement moves the timestamp.
    /// </summary>
    private sealed class MigratedTargets
    {
        private readonly Dictionary<string, (long Length, DateTime LastWriteUtc)> _written =
            new(StringComparer.Ordinal);

        public void Record(string target)
        {
            try
            {
                var info = new FileInfo(target);
                _written[target] = (info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unstattable right after writing it: leave it unrecorded so cleanup keeps
                // the source rather than deleting against an unverifiable copy.
            }
        }

        public bool IsUnchangedSinceThisRunWroteIt(string target)
        {
            if (!_written.TryGetValue(target, out var written))
            {
                return false;
            }

            try
            {
                var info = new FileInfo(target);
                return info.Exists
                       && info.Length == written.Length
                       && info.LastWriteTimeUtc == written.LastWriteUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    // Files land at target only via an atomic same-directory rename, so a crash or I/O error
    // mid-copy never leaves a partial file visible there.
    private static void CopyEntry(
        string source,
        string target,
        MigratedTargets migrated,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                CopyEntry(
                    child,
                    Path.Join(target, SafeLeafName(Path.GetFileName(child), nameof(child))),
                    migrated,
                    ct);
            }

            return;
        }

        if (!File.Exists(source))
            return;

        if (File.Exists(target))
        {
            // Something already occupies the name. A size mismatch is definitely not our copy, so
            // fail rather than migrate onto an unrelated file. A size match may be a resumed
            // migration's own output but is not proven to be, so the target is deliberately NOT
            // recorded — costing disk rather than risking the source.
            if (!FileLengthsMatch(source, target))
            {
                throw new IOException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Cannot migrate '{0}': a different file already exists at '{1}'.",
                    source,
                    target));
            }

            return;
        }

        var targetDir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(targetDir);
        var stagingTarget = Path.Join(
            targetDir,
            $".{Path.GetFileName(target)}.tw-migrate-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, stagingTarget);
            File.Move(stagingTarget, target);
            migrated.Record(target);
        }
        catch
        {
            try
            {
                File.Delete(stagingTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort staging cleanup
            }

            throw;
        }
    }


    // Deletes source only once its copy is confirmed at target. Runs only after the settings
    // commit, so a failure wastes disk space but cannot make the active model root incomplete.
    private static void DeleteMigratedEntry(string source, string target, MigratedTargets migrated)
    {
        if (Directory.Exists(source))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                DeleteMigratedEntry(
                    child,
                    Path.Join(target, SafeLeafName(Path.GetFileName(child), nameof(child))),
                    migrated);
            }

            TryDeleteDirectoryIfEmpty(source);
            return;
        }

        // Provenance, not resemblance: only a target this run wrote is known to be the source's
        // copy, and it must still carry the size and timestamp it had when written — a target
        // swapped out between the copy and this cleanup pass spares the source.
        if (!File.Exists(source) || !migrated.IsUnchangedSinceThisRunWroteIt(target))
        {
            return;
        }

        TryDeleteFile(source);
    }

    // The per-entry delete helpers swallow their own I/O failures, but the directory walks
    // around them do not — and cleanup runs after the settings commit, so an unreadable
    // source directory must not surface as a failed migration.
    private static void TryCleanUp(Action cleanUp)
    {
        try
        {
            cleanUp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(
                "Model storage migration cleanup failed: {0}",
                ex.Message);
        }
    }

    private static bool FileLengthsMatch(string source, string target)
    {
        try
        {
            return new FileInfo(source).Length == new FileInfo(target).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(
                "Could not delete migrated source file '{0}': {1}",
                path,
                ex.Message);
        }
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
            Trace.TraceWarning(
                "Could not delete empty model storage directory '{0}': {1}",
                path,
                ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning(
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
