using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.UiTests;

/// <summary>
///     Rows only render when there is something to list, so this seeds a profile, a history entry
///     and a plugin with an expanded settings card, and checks every control a screen reader reaches.
///     It also checks the opened profile editor for clipped text at every window size; clipping
///     elsewhere and stale text are left to <see cref="MainWindowRenderTests" />.
/// </summary>
[Collection("ui")]
public sealed class SeededContentAccessibilityTests(UiSession ui)
{
    public static TheoryData<string> Languages => MainWindowRenderTests.Languages;

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task SeededRows_HaveReadableAccessibleNames(string language)
    {
        var findings = await ui.Session.Dispatch(() => Render(language), CancellationToken.None);

        Assert.True(findings.Count == 0, string.Join("\n", findings.Distinct()));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ProfileEditor_FitsInEverySize(string language)
    {
        var findings = await ui.Session.Dispatch(() => RenderProfileEditor(language), CancellationToken.None);

        Assert.True(findings.Count == 0, string.Join("\n", findings.Distinct()));
    }

    // Clipping is measured across the row, so the scrolled-out part of the editor is checked too.
    private List<string> RenderProfileEditor(string language)
    {
        Loc.Instance.CurrentLanguage = language;
        try
        {
            Seed();
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var findings = new List<string>();
            foreach (var (sizeName, width, height) in MainWindowRenderTests.s_sizes)
            {
                var window = new MainWindow(viewModel) { Width = width, Height = height };
                window.Show();
                try
                {
                    var profiles = viewModel.NavItems.Single(item => item.Content is ProfilesSectionViewModel);
                    viewModel.SelectedItem = profiles;
                    var section = (ProfilesSectionViewModel)profiles.Content!;
                    section.SelectedProfile = section.Profiles.Single(profile => profile.Id == ProfileId);
                    MainWindowRenderTests.Settle();
                    Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), combo =>
                        combo.IsEffectivelyVisible
                        && AutomationProperties.GetName(combo) == Loc.Instance["Profiles.Language"]);
                    window.CaptureRenderedFrame()?.Save(
                        Path.Join(UiEnvironment.SnapshotDir, $"ProfileEditor-{language}-{sizeName}.png"));
                    findings.AddRange(UiInspector.Inspect(window, new HashSet<string>())
                        .Where(finding => finding.Kind == UiInspector.ClippedText)
                        .Select(finding => $"{sizeName}: {finding.Control} {finding.Detail} at {finding.Location}"));
                }
                finally
                {
                    window.Close();
                }
            }

            return findings;
        }
        finally
        {
            try
            {
                Unseed();
            }
            finally
            {
                Loc.Instance.CurrentLanguage = "en";
            }
        }
    }

    private List<string> Render(string language)
    {
        Loc.Instance.CurrentLanguage = language;
        try
        {
            Seed();
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(viewModel) { Width = 1200, Height = 780 };
            window.Show();
            try
            {
                var findings = new List<string>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in viewModel.NavItems.Where(item =>
                             item.Content is ProfilesSectionViewModel or HistorySectionViewModel or PluginsSectionViewModel))
                {
                    viewModel.SelectedItem = item;
                    MainWindowRenderTests.Settle();
                    if (item.Content is PluginsSectionViewModel plugins)
                    {
                        Assert.Equal(PluginRevealResult.Revealed,
                            Await(plugins.RevealPluginAsync(UiSettingsPlugin.Id, Task.CompletedTask, Task.CompletedTask)));
                    }
                    names.UnionWith(window.GetVisualDescendants().OfType<Control>()
                        .Select(AutomationProperties.GetName).OfType<string>());
                    findings.AddRange(UiInspector.Inspect(window, new HashSet<string>())
                        .Where(finding => finding.Kind is UiInspector.UnnamedControl or UiInspector.CodeLikeName)
                        .Select(finding => $"{item.Content!.GetType().Name}: {finding.Kind} {finding.Control} {finding.Detail} at {finding.Location}"));
                }
                // The seeded rows and the expanded settings card were actually rendered.
                Assert.Contains("Email", names);
                Assert.Contains(names, name => name.EndsWith("Hello world.", StringComparison.Ordinal));
                Assert.Contains(Loc.Instance.GetString("Plugins.EnabledToggleName", "Settings Sample"), names);
                Assert.Contains("Endpoint", names);
                return findings;
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            // Cleanup also covers a partial Seed, and the language is reset even if it fails.
            try
            {
                Unseed();
            }
            finally
            {
                Loc.Instance.CurrentLanguage = "en";
            }
        }
    }

    private void Seed()
    {
        ui.Services.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = ProfileId,
            Name = "Email",
            ProcessNames = ["thunderbird"],
            UrlPatterns = ["mail.example.com"],
        });
        ui.Services.GetRequiredService<IHistoryService>().AddRecord(new TranscriptionRecord
        {
            Id = RecordId,
            Timestamp = DateTime.UtcNow,
            RawText = "hello world",
            FinalText = "Hello world.",
        });

        var plugin = new UiSettingsPlugin();
        var directory = Path.Join(UiEnvironment.Root, "plugins", UiSettingsPlugin.Id);
        Directory.CreateDirectory(directory);
        var manifest = new PluginManifest
        {
            Id = UiSettingsPlugin.Id,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "ui-settings.dll",
            PluginClass = typeof(UiSettingsPlugin).FullName!,
            NetworkAccess = PluginNetworkAccess.Local,
            Categories = [PluginCategory.Utility],
        };
        PluginManagerTestAccess.SetLoadedPlugins(
            ui.Services.GetRequiredService<PluginManager>(),
            [new LoadedPlugin(manifest, plugin, new PluginAssemblyLoadContext(directory), directory, PluginLoader.ResolveMetadata(manifest))],
            [UiSettingsPlugin.Id]);
        MainWindowRenderTests.Settle();
    }

    private void Unseed()
    {
        ui.Services.GetRequiredService<IProfileService>().DeleteProfile(ProfileId);
        ui.Services.GetRequiredService<IHistoryService>().DeleteRecord(RecordId);
        PluginManagerTestAccess.SetLoadedPlugins(ui.Services.GetRequiredService<PluginManager>(), [], []);
        MainWindowRenderTests.Settle();
        // The Plugins page dropped the card, so the other render tests never see it.
        Assert.Equal(PluginRevealResult.NotInstalled, Await(ui.Services.GetRequiredService<PluginsSectionViewModel>()
            .RevealPluginAsync(UiSettingsPlugin.Id, Task.CompletedTask, Task.CompletedTask)));
    }

    private static T Await<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(task.IsCompleted, "Timed out after 10 s waiting for the plugin reveal.");
        var result = task.GetAwaiter().GetResult();
        MainWindowRenderTests.Settle();
        return result;
    }

    private const string ProfileId = "ui-seeded-profile";
    private const string RecordId = "ui-seeded-record";
}
