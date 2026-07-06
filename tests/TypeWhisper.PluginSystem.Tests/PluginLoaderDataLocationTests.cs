using System.Reflection;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Verifies that <see cref="PluginLoader" /> wires up <see cref="IPluginDataLocationAware" />
///     plugins by calling <c>SetDataDirectory</c> with a path under
///     <c>PluginData/{manifestId}</c>.
/// </summary>
public sealed class PluginLoaderDataLocationTests : IDisposable
{
    private readonly PluginLoader _loader = new();
    private readonly string _tempDir;

    public PluginLoaderDataLocationTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "tw-loader-data-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }

    [Fact]
    public async Task DiscoverAndLoad_DataLocationAwarePlugin_ReceivesDataDirectoryUnderPluginData()
    {
        // The Script plugin implements IPluginDataLocationAware. Copy its build
        // output into a fake plugin directory so PluginLoader discovers it.
        StageScriptPlugin();

        var loaded = _loader.DiscoverAndLoad([_tempDir]);

        var scriptPlugin = Assert.Single(loaded);
        Assert.Equal("com.typewhisper.script", scriptPlugin.Manifest.Id);

        // The instance must be IPluginDataLocationAware (type identity preserved
        // because TypeWhisper.PluginSDK is a shared contract assembly).
        Assert.IsType<IPluginDataLocationAware>(scriptPlugin.Instance, exactMatch: false);

        // Observable behavior: a collection-settings provider whose data directory
        // was *not* set throws InvalidOperationException from GetItemsAsync.
        // PluginLoader must have called SetDataDirectory, so this does NOT throw.
        var collectionProvider = Assert.IsType<IPluginCollectionSettingsProvider>(
            scriptPlugin.Instance,
            exactMatch: false
        );
        var items = await collectionProvider.GetItemsAsync("scripts");
        Assert.Empty(items);

        // Verify the directory PluginLoader actually passed to SetDataDirectory
        // is rooted at PluginData/{manifestId} by reading it back from the plugin.
        var dataDirField = scriptPlugin
            .Instance.GetType()
            .GetField("_dataDirectory", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(dataDirField);
        var receivedDir = (string?)dataDirField.GetValue(scriptPlugin.Instance);
        Assert.Equal(
            Path.Join(TypeWhisperEnvironment.PluginDataPath, scriptPlugin.Manifest.Id),
            receivedDir
        );
    }

    /// <summary>
    ///     Copies the compiled Script plugin (assembly + PluginSDK + manifest) into a
    ///     subdirectory of the temp search root.
    /// </summary>
    private void StageScriptPlugin()
    {
        var sourceDir = Path.GetDirectoryName(
            typeof(ScriptPlugin).Assembly.Location
        )!;
        var pluginDir = Path.Join(_tempDir, "com.typewhisper.script");
        Directory.CreateDirectory(pluginDir);

        // Copy the plugin assembly.
        var asmName = Path.GetFileName(typeof(ScriptPlugin).Assembly.Location);
        File.Copy(
            Path.Join(sourceDir, asmName),
            Path.Join(pluginDir, asmName),
            true
        );

        // manifest.json — point at the copied assembly.
        const string manifest = """
                                {
                                  "id": "com.typewhisper.script",
                                  "name": "Script Runner",
                                  "version": "1.0.0",
                                  "assemblyName": "TypeWhisper.Plugin.Script.dll",
                                  "pluginClass": "TypeWhisper.Plugin.Script.ScriptPlugin"
                                }
                                """;
        File.WriteAllText(Path.Join(pluginDir, "manifest.json"), manifest);
    }
}