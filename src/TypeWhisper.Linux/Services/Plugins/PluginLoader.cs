using System.Collections.Frozen;
using System.Diagnostics;
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
    string PluginDirectory,
    PluginMetadataDescriptor Metadata
);

public sealed record PluginLoadFailure(string PluginDirectory, string Message);

/// <summary>
///     Validated, normalized plugin metadata consumed throughout the host.
/// </summary>
public sealed class PluginMetadataDescriptor
{
    public PluginMetadataDescriptor(
        PluginNetworkAccess networkAccess,
        IEnumerable<PluginCategory> categories
    )
    {
        NetworkAccess = networkAccess;
        Categories = categories.ToFrozenSet();
        if (Categories.Count == 0)
        {
            throw new ArgumentException(
                "A plugin metadata descriptor requires at least one category.",
                nameof(categories)
            );
        }
    }

    public PluginNetworkAccess NetworkAccess { get; }
    public IReadOnlySet<PluginCategory> Categories { get; }
    public bool RanLocally => NetworkAccess == PluginNetworkAccess.Local;
}

/// <summary>
///     Isolated assembly load context for each plugin, enabling per-plugin
///     dependency resolution. Collectible so plugins can be unloaded.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    // Shared managed contracts must resolve to the host's copy so type identity
    // (e.g. ITypeWhisperPlugin) is preserved across host/plugin boundaries.
    private static readonly string[] s_sharedContractAssemblies = ["TypeWhisper.PluginSDK"];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath)
        : base(true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (
            assemblyName.Name is { } name
            && Array.Exists(
                s_sharedContractAssemblies,
                s => string.Equals(s, name, StringComparison.Ordinal)
            )
        )
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
///     Discovers and loads plugins from subdirectories containing a manifest.json.
///     The Windows "Mark of the Web" unblocking step is a no-op on Linux
///     (no NTFS alternate data streams or SmartScreen).
/// </summary>
public sealed class PluginLoader
{
    private static readonly JsonSerializerOptions s_manifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly List<PluginLoadFailure> _lastLoadFailures = [];

    public PluginLoader(string pluginDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDataRoot);
        PluginDataRoot = Path.GetFullPath(pluginDataRoot);
    }

    public IReadOnlyList<PluginLoadFailure> LastLoadFailures => _lastLoadFailures;
    // Internal deterministic seam for compatibility tests; production uses informational SemVer.
    internal string HostVersion { get; init; } = AppVersion.Display;
    internal string PluginDataRoot { get; }

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
                    if (plugin is null)
                    {
                        continue;
                    }

                    loaded.Add(plugin);
                    Trace.WriteLine(
                        $"[PluginLoader] Loaded plugin: {plugin.Manifest.Id} v{plugin.Manifest.Version} from {pluginDir}"
                    );
                }
                catch (Exception ex)
                {
                    _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, ex.Message));
                    Trace.WriteLine(
                        $"[PluginLoader] Failed to load plugin from {pluginDir}: {ex.Message}"
                    );
                }
            }
        }

        return loaded;
    }

    internal LoadedPlugin? LoadPlugin(string pluginDir)
    {
        var manifestPath = Path.Join(pluginDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Trace.WriteLine($"[PluginLoader] No manifest.json in {pluginDir}, skipping");
            return null;
        }

        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            manifestJson,
            s_manifestJsonOptions
        );
        if (manifest is null)
        {
            _lastLoadFailures.Add(
                new PluginLoadFailure(pluginDir, "Failed to deserialize manifest.json.")
            );
            Trace.WriteLine($"[PluginLoader] Failed to deserialize manifest in {pluginDir}");
            return null;
        }

        var metadata = ResolveMetadata(manifest);

        if (
            !AppVersion.IsHostCompatible(
                manifest.MinHostVersion,
                HostVersion,
                out var incompatibilityReason
            )
        )
        {
            var message =
                $"Plugin '{manifest.Id}' is incompatible with this host: {incompatibilityReason}";
            _lastLoadFailures.Add(new PluginLoadFailure(pluginDir, message));
            Trace.WriteLine($"[PluginLoader] {message}");
            return null;
        }

        var assemblyPath = Path.Join(pluginDir, manifest.AssemblyName);
        if (!File.Exists(assemblyPath))
        {
            _lastLoadFailures.Add(
                new PluginLoadFailure(pluginDir, $"Assembly not found: {manifest.AssemblyName}")
            );
            Trace.WriteLine($"[PluginLoader] Assembly not found: {assemblyPath}");
            return null;
        }

        var loadContext = new PluginAssemblyLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        var pluginType = assembly.GetType(manifest.PluginClass);
        if (pluginType is null)
        {
            _lastLoadFailures.Add(
                new PluginLoadFailure(pluginDir, $"Plugin class not found: {manifest.PluginClass}")
            );
            Trace.WriteLine(
                $"[PluginLoader] Plugin class '{manifest.PluginClass}' not found in {assemblyPath}"
            );
            loadContext.Unload();
            return null;
        }

        if (!typeof(ITypeWhisperPlugin).IsAssignableFrom(pluginType))
        {
            _lastLoadFailures.Add(
                new PluginLoadFailure(
                    pluginDir,
                    $"Class does not implement ITypeWhisperPlugin: {manifest.PluginClass}"
                )
            );
            Trace.WriteLine(
                $"[PluginLoader] Class '{manifest.PluginClass}' does not implement ITypeWhisperPlugin"
            );
            loadContext.Unload();
            return null;
        }

        ITypeWhisperPlugin? instance;
        try
        {
            instance = Activator.CreateInstance(pluginType) as ITypeWhisperPlugin;
        }
        catch (Exception ex)
        {
            _lastLoadFailures.Add(
                new PluginLoadFailure(
                    pluginDir,
                    $"Plugin constructor threw: {ex.GetBaseException().Message}"
                )
            );
            Trace.WriteLine($"[PluginLoader] Constructor of '{manifest.PluginClass}' threw: {ex}");
            loadContext.Unload();
            return null;
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement — the following
        // `instance is IPluginDataLocationAware` / `IPluginLocalizationAware` checks are
        // independent (a plugin may implement both); a type-switch would skip later matches.
        if (instance is null)
        {
            _lastLoadFailures.Add(
                new PluginLoadFailure(
                    pluginDir,
                    $"Failed to create plugin instance: {manifest.PluginClass}"
                )
            );
            Trace.WriteLine(
                $"[PluginLoader] Failed to create instance of '{manifest.PluginClass}'"
            );
            loadContext.Unload();
            return null;
        }

        // Plugins that need a stable writable directory (models, caches) declare
        // IPluginDataLocationAware and receive the path before ActivateAsync.
        // ReSharper disable once SuspiciousTypeConversion.Global -- the plugin instance is loaded from an external assembly (AssemblyLoadContext) that implements this capability interface; the cross-assembly implementer is not visible in-solution.
        if (instance is IPluginDataLocationAware dataLocationAware)
        {
            dataLocationAware.SetDataDirectory(
                Path.Join(PluginDataRoot, manifest.Id)
            );
        }

        // Plugins that localize their settings UI declare IPluginLocalizationAware
        // and receive their catalog at load — before, and independent of,
        // activation. The settings page queries metadata (labels, validation) for
        // every discovered plugin, including disabled ones that are never
        // activated, so localization must not depend on _host being set.
        // ReSharper disable once SuspiciousTypeConversion.Global -- the plugin instance is loaded from an external assembly (AssemblyLoadContext) that implements this capability interface; the cross-assembly implementer is not visible in-solution.
        if (instance is IPluginLocalizationAware localizationAware)
        {
            localizationAware.SetLocalization(new PluginLocalization(pluginDir));
        }

        return new LoadedPlugin(manifest, instance, loadContext, pluginDir, metadata);
    }

    internal static PluginMetadataDescriptor ResolveMetadata(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var networkAccess = manifest.NetworkAccess;
        if (networkAccess is { } declaredNetworkAccess)
        {
            if (!Enum.IsDefined(declaredNetworkAccess))
            {
                throw new InvalidDataException(
                    $"Plugin '{manifest.Id}' declares an invalid networkAccess value."
                );
            }
        }
        else
        {
            networkAccess = PluginLocalityClassifier.ResolveLegacy(manifest);
        }

        var categories = manifest.Categories;
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- independent guard clauses with unrelated outcomes (throw vs legacy inference); a switch would obscure that.
        if (categories is { Length: 0 })
        {
            throw new InvalidDataException(
                $"Plugin '{manifest.Id}' declares an empty categories array."
            );
        }

        if (categories is null)
        {
            return new PluginMetadataDescriptor(
                networkAccess.Value,
                [InferLegacyCategory(manifest)]
            );
        }

        if (
            categories.Any(category =>
                !Enum.IsDefined(category) || category == PluginCategory.Unknown
            )
        )
        {
            throw new InvalidDataException(
                $"Plugin '{manifest.Id}' declares an invalid category."
            );
        }

        return new PluginMetadataDescriptor(networkAccess.Value, categories);
    }

    private static PluginCategory InferLegacyCategory(PluginManifest manifest)
    {
        var id = manifest.Id.Trim().ToLowerInvariant();
        if (s_legacyTranscriptionPluginIds.Contains(id))
        {
            return PluginCategory.Transcription;
        }

        if (s_legacyLlmPluginIds.Contains(id))
        {
            return PluginCategory.Llm;
        }

        if (s_legacyActionPluginIds.Contains(id))
        {
            return PluginCategory.Action;
        }

        if (s_legacyMemoryPluginIds.Contains(id))
        {
            return PluginCategory.Memory;
        }

        if (s_legacyUtilityPluginIds.Contains(id))
        {
            return PluginCategory.Utility;
        }

        var combined = $"{manifest.Name} {manifest.Description}".ToLowerInvariant();
        if (
            combined.Contains("transcription")
            || combined.Contains("speech-to-text")
            || combined.Contains("speech to text")
            || combined.Contains("asr")
        )
        {
            return PluginCategory.Transcription;
        }

        if (
            combined.Contains("llm")
            || combined.Contains("prompt")
            || combined.Contains("inference")
            || combined.Contains("multi-model")
        )
        {
            return PluginCategory.Llm;
        }

        if (combined.Contains("text-to-speech") || combined.Contains("tts"))
        {
            return PluginCategory.Tts;
        }

        if (combined.Contains("memory"))
        {
            return PluginCategory.Memory;
        }

        if (combined.Contains("webhook"))
        {
            return PluginCategory.Integration;
        }

        if (
            combined.Contains("issue")
            || combined.Contains("obsidian")
            || combined.Contains("script")
        )
        {
            return PluginCategory.Action;
        }

        return PluginCategory.Unknown;
    }

    private static readonly FrozenSet<string> s_legacyTranscriptionPluginIds =
        new[]
        {
            "com.typewhisper.assemblyai",
            "com.typewhisper.cloudflare-asr",
            "com.typewhisper.deepgram",
            "com.typewhisper.gladia",
            "com.typewhisper.google-cloud-stt",
            "com.typewhisper.openai",
            "com.typewhisper.qwen3-stt",
            "com.typewhisper.sherpa-onnx",
            "com.typewhisper.soniox",
            "com.typewhisper.speechmatics",
            "com.typewhisper.voxtral",
            "com.typewhisper.whisper-cpp",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> s_legacyLlmPluginIds =
        new[]
        {
            "com.typewhisper.cerebras",
            "com.typewhisper.claude",
            "com.typewhisper.cohere",
            "com.typewhisper.fireworks",
            "com.typewhisper.gemini",
            "com.typewhisper.gemma-local",
            "com.typewhisper.groq",
            "com.typewhisper.openai-compatible",
            "com.typewhisper.openrouter",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> s_legacyActionPluginIds =
        new[]
        {
            "com.typewhisper.linear",
            "com.typewhisper.obsidian",
            "com.typewhisper.script",
            "com.typewhisper.webhook",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> s_legacyMemoryPluginIds =
        new[]
        {
            "com.typewhisper.file-memory",
            "com.typewhisper.openai-vector-memory",
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> s_legacyUtilityPluginIds =
        new[]
        {
            "com.typewhisper.openai-compatible",
        }.ToFrozenSet(StringComparer.Ordinal);
}
