using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Tests;

internal static class TestPluginManagerFactory
{
    public static PluginManager Create(
        IReadOnlyList<ILlmProviderPlugin>? llmProviders = null,
        IReadOnlyList<IActionPlugin>? actionPlugins = null,
        IReadOnlyList<LoadedPlugin>? loadedPlugins = null)
    {
        var activeWindow = new Mock<IActiveWindowService>();
        var profiles = new Mock<IProfileService>();
        var settings = CreateSettings(new AppSettings());
        profiles.SetupGet(service => service.Profiles).Returns([]);

        var pluginManager = new PluginManager(
            new PluginLoader(),
            new PluginEventBus(),
            activeWindow.Object,
            profiles.Object,
            settings.Object);

        if (llmProviders is not null)
            SetPrivateField(pluginManager, "_llmProviders", llmProviders.ToList());
        if (actionPlugins is not null)
            SetPrivateField(pluginManager, "_actionPlugins", actionPlugins.ToList());
        if (loadedPlugins is not null)
            SetPrivateField(pluginManager, "_allPlugins", loadedPlugins.ToList());

        return pluginManager;
    }

    public static Mock<ISettingsService> CreateSettings(AppSettings current)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(current);
        settings.Setup(service => service.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(saved => settings.SetupGet(service => service.Current).Returns(saved));
        return settings;
    }

    public static LoadedPlugin CreateLoadedPlugin(string pluginDir, string pluginId, ITypeWhisperPlugin plugin) =>
        new(
            new PluginManifest
            {
                Id = pluginId,
                Name = plugin.PluginName,
                Version = plugin.PluginVersion,
                AssemblyName = "fake.dll",
                PluginClass = plugin.GetType().FullName ?? plugin.GetType().Name
            },
            plugin,
            new PluginAssemblyLoadContext(pluginDir),
            pluginDir);

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }
}
