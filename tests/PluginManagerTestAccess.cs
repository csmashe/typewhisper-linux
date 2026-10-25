using System.Reflection;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Tests;

// Linked into the test projects that fake plugin discovery rather than owned by one of them.
internal static class PluginManagerTestAccess
{
    public static void SetTranscriptionEngines(
        PluginManager pluginManager,
        IReadOnlyList<ITranscriptionEngineRole> engines
    )
    {
        var field =
            typeof(PluginManager).GetField(
                "_transcriptionEngines",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new MissingFieldException(
                typeof(PluginManager).FullName,
                "_transcriptionEngines"
            );
        field.SetValue(pluginManager, engines.ToList());
    }

    /// <summary>Seeds discovered plugins and marks <paramref name="activatedIds" /> as enabled.</summary>
    // ReSharper disable once UnusedMember.Global -- used from the UI tests; the Linux tests seed through TestPluginManagerFactory.
    public static void SetLoadedPlugins(
        PluginManager pluginManager,
        IReadOnlyList<LoadedPlugin> plugins,
        IEnumerable<string> activatedIds
    )
    {
        SetField(pluginManager, "_allPlugins", plugins.ToList());
        SetField(pluginManager, "_activatedPlugins", new HashSet<string>(activatedIds));
        // Tell listeners such as the Plugins page, without rebuilding the seeded capability lists.
        var handler = (EventHandler?)typeof(PluginManager)
            .GetField(nameof(PluginManager.PluginStateChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pluginManager);
        handler?.Invoke(pluginManager, EventArgs.Empty);
    }

    private static void SetField(PluginManager pluginManager, string name, object value) =>
        (typeof(PluginManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PluginManager).FullName, name))
        .SetValue(pluginManager, value);

    // ReSharper disable once UnusedMember.Global -- used from the Linux tests; the UI tests link this file only for SetTranscriptionEngines and SetLoadedPlugins.
    public static void SetVocabularyRescorers(
        PluginManager pluginManager,
        IReadOnlyList<IVocabularyRescorerPlugin> engines
    )
    {
        var field =
            typeof(PluginManager).GetField(
                "_vocabularyRescorers",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new MissingFieldException(
                typeof(PluginManager).FullName,
                "_vocabularyRescorers"
            );
        field.SetValue(pluginManager, engines.ToList());
    }
}
