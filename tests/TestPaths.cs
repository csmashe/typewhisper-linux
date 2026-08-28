// Linked into each test project rather than owned by one, so solution-wide analysis reports
// the linked-in copies as unused. Every member here is called from the suites that link it.
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

using TypeWhisper.Core;

namespace TypeWhisper.Tests;

// File-linked into four test projects; TypeWhisper.Integration.Tests links it without referencing it yet,
// so solution-wide analysis reports that copy of the class and its members as unused.
// ReSharper disable once UnusedType.Global
internal static class TestPaths
{
    // ReSharper disable once UnusedMember.Global -- see the note on the class; used from the other linked projects.
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
        // Keep generated paths inside the temp dir. EnsureIsolated only blocks the production
        // root, so a rooted name or one with separators could otherwise escape via Path.Join /
        // GetFullPath (e.g. a "../.." name).
        if (Path.IsPathRooted(name)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "Temp path name must not be rooted or contain directory separators.", nameof(name));
        }

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

    // ReSharper disable once UnusedMember.Global -- see the note on the class; used from the other linked projects.
    public static void DeleteDirectory(string path)
    {
        var isolatedPath = EnsureIsolated(path);
        if (Directory.Exists(isolatedPath))
        {
            Directory.Delete(isolatedPath, recursive: true);
        }
    }
}
