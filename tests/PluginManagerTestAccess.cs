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
}
