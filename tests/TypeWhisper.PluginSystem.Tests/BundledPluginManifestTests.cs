using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class BundledPluginManifestTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void AllBundledManifests_DeclareCompleteNormalizedMetadata()
    {
        var manifestPaths = ManifestPaths();

        Assert.Equal(32, manifestPaths.Length);
        foreach (var path in manifestPaths)
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.True(
                root.TryGetProperty("networkAccess", out var networkAccessElement),
                $"{path} does not declare networkAccess."
            );
            Assert.Equal(JsonValueKind.String, networkAccessElement.ValueKind);
            Assert.True(
                Enum.TryParse<PluginNetworkAccess>(
                    networkAccessElement.GetString(),
                    true,
                    out var declaredNetworkAccess
                )
                && Enum.IsDefined(declaredNetworkAccess),
                $"{path} declares an invalid networkAccess."
            );

            Assert.True(
                root.TryGetProperty("categories", out var categoriesElement),
                $"{path} does not declare categories."
            );
            Assert.Equal(JsonValueKind.Array, categoriesElement.ValueKind);
            var categoryValues = categoriesElement.EnumerateArray().ToArray();
            Assert.NotEmpty(categoryValues);
            foreach (var categoryElement in categoryValues)
            {
                Assert.Equal(JsonValueKind.String, categoryElement.ValueKind);
                Assert.True(
                    Enum.TryParse<PluginCategory>(
                        categoryElement.GetString(),
                        true,
                        out var category
                    )
                    && Enum.IsDefined(category)
                    && category != PluginCategory.Unknown,
                    $"{path} declares an invalid category."
                );
            }

            Assert.False(
                root.TryGetProperty("category", out _),
                $"{path} still declares the legacy category field."
            );
            Assert.False(
                root.TryGetProperty("isLocal", out _),
                $"{path} still declares the legacy isLocal field."
            );

            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, s_jsonOptions);
            Assert.NotNull(manifest);
            Assert.Equal(declaredNetworkAccess, manifest.NetworkAccess);
            Assert.NotNull(manifest.Categories);
            Assert.NotEmpty(manifest.Categories);
        }
    }

    [Fact]
    public void Webhook_IsUserControlledAndNeverLocal()
    {
        var path = Assert.Single(
            ManifestPaths(),
            candidate =>
                candidate.EndsWith(
                    Path.Join("TypeWhisper.Plugin.Webhook", "manifest.json"),
                    StringComparison.Ordinal
                )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(path),
            s_jsonOptions
        );

        Assert.NotNull(manifest);
        Assert.Equal(PluginNetworkAccess.UserControlled, manifest.NetworkAccess);
        Assert.NotEqual(PluginNetworkAccess.Local, manifest.NetworkAccess);
        Assert.Equal([PluginCategory.Integration], manifest.Categories);
    }

    private static string[] ManifestPaths(
        [CallerFilePath] string thisFile = ""
    )
    {
        var testDirectory = Path.GetDirectoryName(thisFile)!;
        var pluginsDirectory = Path.GetFullPath(
            Path.Join(testDirectory, "..", "..", "plugins")
        );
        return Directory
            .EnumerateDirectories(pluginsDirectory, "TypeWhisper.Plugin.*")
            .Select(directory => Path.Join(directory, "manifest.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
