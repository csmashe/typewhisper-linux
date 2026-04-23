using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

public sealed record LoadedPlugin(
    PluginManifest Manifest,
    ITypeWhisperPlugin Instance,
    PluginAssemblyLoadContext LoadContext,
    string PluginDirectory);

public sealed record PluginLoadFailure(
    string PluginDirectory,
    string Message);

/// <summary>
/// Isolated assembly load context for each plugin, enabling per-plugin
/// dependency resolution. Collectible so plugins can be unloaded.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    // Shared managed contracts must resolve to the host's copy so type identity
    // (e.g. ITypeWhisperPlugin) is preserved across host/plugin boundaries.
    private static readonly string[] SharedContractAssemblies =
    [
        "TypeWhisper.PluginSDK",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name
            && Array.Exists(SharedContractAssemblies, s => string.Equals(s, name, StringComparison.Ordinal)))
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    // No shared unmanaged contracts exist: native libs (e.g. libwhisper) are
    // genuinely plugin-private, so the resolver is authoritative here.
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

/// <summary>
/// Discovers and loads plugins from one or more search directories.
/// Each plugin resides in a subdirectory containing a manifest.json file.
///
/// Linux port: the Windows "Mark of the Web" unblocking step is a no-op
/// here — Linux doesn't have NTFS alternate data streams or SmartScreen.
/// </summary>
public sealed class PluginLoader
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<PluginLoadFailure> LastLoadFailures => _lastLoadFailures;
    private readonly List<PluginLoadFailure> _lastLoadFailures = [];

    public List<LoadedPlugin> DiscoverAndLoad(IEnumerable<string> searchDirectories)
    {
        var loaded = new List<LoadedPlugin>();
        _lastLoadFailures.Clear();

        foreach (var searchDir in searchDirectories)
        {
            if (!Directory.Exists(searchDir))
            {
                Trace.WriteLine($"[PluginLoader] Search directory does not exist: {searchDir}");
                continue;
            }

            foreach (var pluginDir in Directory.GetDirectories(searchDir))
            {
                try
                {
                    var plugin = LoadPlugin(pluginDir);
                    if (plugin is not null)
                    {
                        loaded.Add(plugin);
                        Trace.WriteLine($"[PluginLoader] Loaded plugin: {plugin.Manifest.Id} v{plugin.Manifest.Version} from {pluginDir}");
                    }
                }
                catch (Exception ex)
                {
                    _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, ex.Message));
                    Trace.WriteLine($"[PluginLoader] Failed to load plugin from {pluginDir}: {ex.Message}");
                }
            }
        }

        return loaded;
    }

    internal LoadedPlugin? LoadPlugin(string pluginDir)
    {
        var manifestPath = Path.Combine(pluginDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Trace.WriteLine($"[PluginLoader] No manifest.json in {pluginDir}, skipping");
            return null;
        }

        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, ManifestJsonOptions);
        if (manifest is null)
        {
            _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, "Failed to deserialize manifest.json."));
            Trace.WriteLine($"[PluginLoader] Failed to deserialize manifest in {pluginDir}");
            return null;
        }

        var assemblyPath = Path.Combine(pluginDir, manifest.AssemblyName);
        if (!File.Exists(assemblyPath))
        {
            _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, $"Assembly not found: {manifest.AssemblyName}"));
            Trace.WriteLine($"[PluginLoader] Assembly not found: {assemblyPath}");
            return null;
        }

        var loadContext = new PluginAssemblyLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        var pluginType = assembly.GetType(manifest.PluginClass);
        if (pluginType is null)
        {
            _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, $"Plugin class not found: {manifest.PluginClass}"));
            Trace.WriteLine($"[PluginLoader] Plugin class '{manifest.PluginClass}' not found in {assemblyPath}");
            loadContext.Unload();
            return null;
        }

        if (!typeof(ITypeWhisperPlugin).IsAssignableFrom(pluginType))
        {
            _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, $"Class does not implement ITypeWhisperPlugin: {manifest.PluginClass}"));
            Trace.WriteLine($"[PluginLoader] Class '{manifest.PluginClass}' does not implement ITypeWhisperPlugin");
            loadContext.Unload();
            return null;
        }

        var instance = Activator.CreateInstance(pluginType) as ITypeWhisperPlugin;
        if (instance is null)
        {
            _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, $"Failed to create plugin instance: {manifest.PluginClass}"));
            Trace.WriteLine($"[PluginLoader] Failed to create instance of '{manifest.PluginClass}'");
            loadContext.Unload();
            return null;
        }

        return new LoadedPlugin(manifest, instance, loadContext, pluginDir);
    }
}
