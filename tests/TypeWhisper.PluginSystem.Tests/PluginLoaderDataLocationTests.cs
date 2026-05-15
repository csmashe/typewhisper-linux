using System.IO;
using System.Reflection;
using TypeWhisper.Core;
using TypeWhisper.PluginSDK;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
/// Verifies that <see cref="PluginLoader"/> wires up <see cref="IPluginDataLocationAware"/>
/// plugins by calling <c>SetDataDirectory</c> with a path under
/// <c>PluginData/{manifestId}</c>.
/// </summary>
public class PluginLoaderDataLocationTests : IDisposable
{
    private readonly PluginLoader _loader = new();
    private readonly string _tempDir;

    public PluginLoaderDataLocationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tw-loader-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
        Assert.IsAssignableFrom<IPluginDataLocationAware>(scriptPlugin.Instance);

        // Observable behavior: a collection-settings provider whose data directory
        // was *not* set throws InvalidOperationException from GetItemsAsync.
        // PluginLoader must have called SetDataDirectory, so this does NOT throw.
        var collectionProvider = Assert.IsAssignableFrom<IPluginCollectionSettingsProvider>(scriptPlugin.Instance);
        var items = await collectionProvider.GetItemsAsync("scripts");
        Assert.Empty(items);

        // Verify the directory PluginLoader actually passed to SetDataDirectory
        // is rooted at PluginData/{manifestId} by reading it back from the plugin.
        var dataDirField = scriptPlugin.Instance.GetType()
            .GetField("_dataDirectory", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(dataDirField);
        var receivedDir = (string?)dataDirField.GetValue(scriptPlugin.Instance);
        Assert.Equal(
            Path.Combine(TypeWhisperEnvironment.PluginDataPath, scriptPlugin.Manifest.Id),
            receivedDir);
    }

    /// <summary>
    /// Copies the compiled Script plugin (assembly + PluginSDK + manifest) into a
    /// subdirectory of the temp search root and returns that directory.
    /// </summary>
    private string StageScriptPlugin()
    {
        var sourceDir = Path.GetDirectoryName(typeof(Plugin.Script.ScriptPlugin).Assembly.Location)!;
        var pluginDir = Path.Combine(_tempDir, "com.typewhisper.script");
        Directory.CreateDirectory(pluginDir);

        // Copy the plugin assembly.
        var asmName = Path.GetFileName(typeof(Plugin.Script.ScriptPlugin).Assembly.Location);
        File.Copy(Path.Combine(sourceDir, asmName), Path.Combine(pluginDir, asmName), overwrite: true);

        // manifest.json — point at the copied assembly.
        var manifest = """
        {
          "id": "com.typewhisper.script",
          "name": "Script Runner",
          "version": "1.0.0",
          "assemblyName": "TypeWhisper.Plugin.Script.dll",
          "pluginClass": "TypeWhisper.Plugin.Script.ScriptPlugin"
        }
        """;
        File.WriteAllText(Path.Combine(pluginDir, "manifest.json"), manifest);

        return pluginDir;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }
}
