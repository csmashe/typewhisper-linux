using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class PluginActionConfirmationTests(UiSession ui)
{
    [Fact]
    public async Task ConfirmableAction_BindsWithoutErrorsBeforeDuringAndAfterConfirmation()
    {
        await ui.Session.Dispatch(() =>
        {
            var plugin = new ConfirmablePlugin();
            Seed(plugin);
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var plugins = ui.Services.GetRequiredService<PluginsSectionViewModel>();
            var window = new MainWindow(viewModel) { Width = 1200, Height = 780 };
            var sink = new BindingErrorSink(Logger.Sink);
            Logger.Sink = sink;
            window.Show();
            try
            {
                viewModel.SelectedItem = viewModel.NavItems.Single(item => ReferenceEquals(item.Content, plugins));
                MainWindowRenderTests.Settle();
                Assert.Equal(PluginRevealResult.Revealed,
                    Await(plugins.RevealPluginAsync(ConfirmablePlugin.Id, Task.CompletedTask, Task.CompletedTask)));
                var row = plugins.EnabledGroups.SelectMany(group => group.Plugins).Single(p => p.Id == ConfirmablePlugin.Id);
                var action = Assert.Single(row.Actions);

                // The normal state that used to log errors: nothing awaiting confirmation.
                Assert.Null(row.PendingAction);
                Assert.Null(ConfirmButton(window, plugins));

                plugins.RunSettingsActionCommand.Execute(action);
                MainWindowRenderTests.Settle();
                Assert.Same(action, row.PendingAction);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text is { Text: ConfirmablePlugin.Message, IsEffectivelyVisible: true });
                var confirm = ConfirmButton(window, plugins);
                Assert.NotNull(confirm);
                Assert.Equal(ConfirmablePlugin.Label, confirm.Content);

                Click(CancelButton(window, plugins));
                Assert.Null(row.PendingAction);
                Assert.Equal(0, plugin.Runs);

                plugins.RunSettingsActionCommand.Execute(action);
                MainWindowRenderTests.Settle();
                Click(ConfirmButton(window, plugins));
                WaitUntil(() => plugin.Runs == 1 && !row.IsActionRunning);
                Assert.Null(row.PendingAction);

                Assert.True(sink.Errors.Count == 0, string.Join("\n", sink.Errors));
            }
            finally
            {
                Logger.Sink = sink.Inner;
                window.Close();
                Unseed();
            }
        }, CancellationToken.None);
    }

    private static Button? ConfirmButton(Window window, PluginsSectionViewModel plugins) =>
        VisibleButton(window, plugins.ConfirmSettingsActionCommand);

    private static Button? CancelButton(Window window, PluginsSectionViewModel plugins) =>
        VisibleButton(window, plugins.DismissSettingsActionConfirmationCommand);

    private static Button? VisibleButton(Window window, object command) =>
        window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(button => ReferenceEquals(button.Command, command) && button.IsEffectivelyVisible);

    private static void Click(Button? button)
    {
        Assert.NotNull(button);
        Assert.True(button.Command!.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
        MainWindowRenderTests.Settle();
    }

    private void Seed(ConfirmablePlugin plugin)
    {
        var directory = Path.Join(UiEnvironment.Root, "plugins", ConfirmablePlugin.Id);
        Directory.CreateDirectory(directory);
        var manifest = new PluginManifest
        {
            Id = ConfirmablePlugin.Id,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "ui-confirmable.dll",
            PluginClass = typeof(ConfirmablePlugin).FullName!,
            NetworkAccess = PluginNetworkAccess.Local,
            Categories = [PluginCategory.Utility],
        };
        PluginManagerTestAccess.SetLoadedPlugins(
            ui.Services.GetRequiredService<PluginManager>(),
            [new LoadedPlugin(manifest, plugin, new PluginAssemblyLoadContext(directory), directory, PluginLoader.ResolveMetadata(manifest))],
            [ConfirmablePlugin.Id]);
        MainWindowRenderTests.Settle();
    }

    private void Unseed()
    {
        PluginManagerTestAccess.SetLoadedPlugins(ui.Services.GetRequiredService<PluginManager>(), [], []);
        MainWindowRenderTests.Settle();
    }

    private static T Await<T>(Task<T> task)
    {
        WaitUntil(() => task.IsCompleted);
        var result = task.GetAwaiter().GetResult();
        MainWindowRenderTests.Settle();
        return result;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(condition(), "Timed out after 10 s.");
        MainWindowRenderTests.Settle();
    }

    private sealed class ConfirmablePlugin : ITypeWhisperPlugin, IPluginSettingsActions
    {
        internal const string Id = "com.typewhisper.ui-confirmable";
        internal const string Label = "Remove data";
        internal const string Message = "Remove all downloaded data?";

        public int Runs { get; private set; }

        public string PluginId => Id;
        public string PluginName => "Confirmable Sample";
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }

        public IReadOnlyList<PluginSettingsAction> GetSettingsActions() =>
            [new("remove", Label, ConfirmationMessage: Message)];

        public Task<PluginSettingsValidationResult> ExecuteSettingsActionAsync(string actionId, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(new PluginSettingsValidationResult(true, "Removed."));
        }
    }
}
