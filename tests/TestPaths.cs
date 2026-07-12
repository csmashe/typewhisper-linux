using TypeWhisper.Core;

namespace TypeWhisper.Tests;

internal static class TestPaths
{
    public static string CreateTempDirectory(string name)
    {
        var path = NewTempPath(name);
        Directory.CreateDirectory(path);
        return path;
    }

    // File-linked into multiple test projects; called externally (e.g. PluginManagerTests, ModelManagerServiceTests) so cannot be private.
    // ReSharper disable once MemberCanBePrivate.Global
    public static string NewTempPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return EnsureIsolated(
            Path.Join(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}")
        );
    }

    // File-linked into multiple test projects; called externally (e.g. TestPathsTests, PluginLoaderDataLocationTests) so cannot be private.
    // ReSharper disable once MemberCanBePrivate.Global
    public static string EnsureIsolated(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var productionRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(TypeWhisperEnvironment.BasePath)
        );
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (
            string.Equals(fullPath, productionRoot, comparison)
            || fullPath.StartsWith(productionRoot + Path.DirectorySeparatorChar, comparison)
        )
        {
            throw new InvalidOperationException(
                $"Tests must not operate inside the production TypeWhisper root: {fullPath}"
            );
        }

        return fullPath;
    }

    public static void DeleteDirectory(string path)
    {
        var isolatedPath = EnsureIsolated(path);
        if (Directory.Exists(isolatedPath))
        {
            Directory.Delete(isolatedPath, recursive: true);
        }
    }
}
