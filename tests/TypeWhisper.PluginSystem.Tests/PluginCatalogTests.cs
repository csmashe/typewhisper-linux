using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginCatalogTests
{
    [Fact]
    public void Catalog_MatchesManifestsAndPluginProjects()
    {
        var catalog = PluginCatalogTestData.Catalog;
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(32, catalog.Plugins.Length);

        var catalogProjectPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Plugins)
        {
            Assert.True(
                catalogProjectPaths.Add(entry.ProjectPath),
                $"Duplicate catalog projectPath: {entry.ProjectPath}"
            );

            var projectPath = PluginCatalogTestData.GetFullPath(entry.ProjectPath);
            Assert.True(File.Exists(projectPath), $"Catalog project does not exist: {projectPath}");

            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectDirectoryName = Path.GetFileName(Path.GetDirectoryName(projectPath));
            Assert.Equal(projectDirectoryName, projectName);

            var manifestPath = Path.Join(Path.GetDirectoryName(projectPath), "manifest.json");
            Assert.True(File.Exists(manifestPath), $"Manifest does not exist: {manifestPath}");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Equal(entry.Id, manifest.RootElement.GetProperty("id").GetString());
        }

        var filesystemProjectPaths = Directory
            .EnumerateDirectories(
                PluginCatalogTestData.PluginsDirectory,
                "TypeWhisper.Plugin.*",
                SearchOption.TopDirectoryOnly
            )
            .SelectMany(directory =>
                Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            )
            .Select(path =>
                Path.GetRelativePath(PluginCatalogTestData.RepositoryRoot, path).Replace('\\', '/')
            )
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            filesystemProjectPaths,
            catalogProjectPaths.Order(StringComparer.Ordinal).ToArray()
        );
    }

    [Fact]
    public void Catalog_DoesNotCarryPluginVersions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(PluginCatalogTestData.CatalogPath));
        foreach (var entry in document.RootElement.GetProperty("plugins").EnumerateArray())
        {
            Assert.False(entry.TryGetProperty("version", out _));
        }
    }
}

public sealed class PluginCatalogParityTests
{
    public static TheoryData<string> CatalogEntries =>
        [.. PluginCatalogTestData.Catalog.Plugins.Select(entry => entry.Id)];

    [Theory]
    [MemberData(nameof(CatalogEntries))]
    public void CatalogParity_PluginVersionAndEntryPointMatchManifest(string catalogId)
    {
        var entry = Assert.Single(
            PluginCatalogTestData.Catalog.Plugins,
            candidate => string.Equals(candidate.Id, catalogId, StringComparison.Ordinal)
        );
        var projectPath = PluginCatalogTestData.GetFullPath(entry.ProjectPath);
        var manifestPath = Path.Join(Path.GetDirectoryName(projectPath), "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestRoot = manifest.RootElement;
        var manifestId = manifestRoot.GetProperty("id").GetString();
        var manifestVersion = manifestRoot.GetProperty("version").GetString();
        var assemblyName = manifestRoot.GetProperty("assemblyName").GetString()
            ?? throw new InvalidOperationException($"Manifest assemblyName is null: {manifestPath}");
        var pluginClass = manifestRoot.GetProperty("pluginClass").GetString()
            ?? throw new InvalidOperationException($"Manifest pluginClass is null: {manifestPath}");

        Assert.False(string.IsNullOrWhiteSpace(assemblyName));
        Assert.False(string.IsNullOrWhiteSpace(pluginClass));
        var assemblyPath = Path.Join(AppContext.BaseDirectory, assemblyName);
        Assert.True(File.Exists(assemblyPath), $"Built plugin assembly does not exist: {assemblyPath}");

        var assembly = Assembly.LoadFrom(assemblyPath);
        // Plugin projects must inherit the repository-root Directory.Build.props
        // (plugins/Directory.Build.props shadows it unless it imports the parent);
        // the deploy script requires host and plugin assembly versions to match.
        Assert.Equal(
            typeof(ITypeWhisperPlugin).Assembly.GetName().Version,
            assembly.GetName().Version
        );
        var entryPoint = assembly.GetType(pluginClass, throwOnError: true)
            ?? throw new InvalidOperationException($"Plugin entry point does not exist: {pluginClass}");
        Assert.True(
            typeof(ITypeWhisperPlugin).IsAssignableFrom(entryPoint),
            $"{pluginClass} does not implement {typeof(ITypeWhisperPlugin).FullName}."
        );

        var plugin = Assert.IsType<ITypeWhisperPlugin>(
            Activator.CreateInstance(entryPoint),
            exactMatch: false
        );
        try
        {
            Assert.Equal(catalogId, manifestId);
            Assert.Equal(catalogId, plugin.PluginId);
            Assert.Equal(manifestVersion, plugin.PluginVersion);
        }
        finally
        {
            plugin.Dispose();
        }
    }
}

internal static class PluginCatalogTestData
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string PluginsDirectory { get; } = Path.Join(RepositoryRoot, "plugins");
    public static string CatalogPath { get; } = Path.Join(PluginsDirectory, "catalog.json");
    public static PluginCatalog Catalog { get; } =
        JsonSerializer.Deserialize<PluginCatalog>(File.ReadAllText(CatalogPath), s_jsonOptions)
        ?? throw new InvalidOperationException($"Could not deserialize plugin catalog: {CatalogPath}");

    public static string GetFullPath(string projectPath) => Path.Join(RepositoryRoot, projectPath);

    private static string FindRepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var testDirectory = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Join(testDirectory, "..", ".."));
    }
}

internal sealed record PluginCatalog(
    int SchemaVersion,
    PluginCatalogEntry[] Plugins
);

// The record mirrors the full catalog.json entry schema even though the assertions above only
// read Id and ProjectPath: deserializing every member is what type-checks the rest of the schema
// (an entry whose "platforms" is a bare string instead of an array fails here). The remaining
// members are consumed by scripts/plugin-catalog.ps1 and the PowerShell catalog tests.
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable once ClassNeverInstantiated.Global -- built by JsonSerializer.Deserialize<PluginCatalog>.
internal sealed record PluginCatalogEntry(
    string Id,
    string ProjectPath,
    string ReleaseSlug,
    string[] Platforms,
    string[] Rids,
    string[]? NativeRuntimes,
    string SdkAbi
);
