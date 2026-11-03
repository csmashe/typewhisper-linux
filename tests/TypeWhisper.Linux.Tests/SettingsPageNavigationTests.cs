using System.Reflection;
using TypeWhisper.Linux.Cli;
using TypeWhisper.Linux.Cli.Commands;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SettingsPageNavigationTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsPageNavigationTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "tw-page-nav-" + Guid.NewGuid().ToString("N"));
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
            // Best-effort cleanup for temp test directories.
        }
    }

    [Theory]
    [InlineData("dictation", "dictation")]
    [InlineData("DICTATION", "dictation")]
    [InlineData("text-insertion", "text-insertion")]
    [InlineData("plugin:com.typewhisper.openai", "plugin:com.typewhisper.openai")]
    [InlineData("PLUGIN:com.Example_x-1", "plugin:com.Example_x-1")]
    public void Page_names_parse_to_their_canonical_form(string value, string expected)
    {
        Assert.Equal(expected, SettingsPage.TryParse(value)?.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("settings")]
    [InlineData("home")]
    [InlineData("plugin:")]
    [InlineData("plugin:com/openai")]
    [InlineData("plugin:com openai")]
    [InlineData("plugin:ünïcode")]
    public void Unknown_pages_and_malformed_plugin_ids_are_rejected(string value)
    {
        Assert.Null(SettingsPage.TryParse(value));
    }

    [Fact]
    public void Plugin_ids_are_capped_at_128_characters()
    {
        Assert.NotNull(SettingsPage.TryParse("plugin:" + new string('a', 128)));
        Assert.Null(SettingsPage.TryParse("plugin:" + new string('a', 129)));
    }

    [Fact]
    public void Every_page_names_a_distinct_section()
    {
        var pages = SettingsPage.Names.Select(name => SettingsPage.TryParse(name)!).ToList();

        Assert.Equal(pages.Count, pages.Select(page => page.Section).Distinct().Count());
        Assert.All(pages, page => Assert.EndsWith("SectionViewModel", page.Section.Name));
        Assert.Equal(typeof(PluginsSectionViewModel), SettingsPage.TryParse("plugin:x")!.Section);
    }

    [Fact]
    public void Help_lists_every_page()
    {
        var listed = CommandLineParser.UsageText[(CommandLineParser.UsageText.IndexOf("Pages:", StringComparison.Ordinal) + 6)..]
            .Split([',', '\n', ' '], StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(SettingsPage.Names, listed);
    }

    [Theory]
    [InlineData("--page", "general")]
    [InlineData("--PAGE", "General")]
    [InlineData("--page=general")]
    [InlineData("--page", "general", "--future-flag")]
    [InlineData("--future-flag", "--page", "general")]
    public void Page_option_launches_the_gui_on_that_page(params string[] args)
    {
        var action = CommandLineParser.Parse(args);

        Assert.Equal(CliActionKind.LaunchGui, action.Kind);
        Assert.Equal("general", action.Page?.Name);
        Assert.False(action.StartMinimized);
    }

    [Fact]
    public void Plugin_page_keeps_the_plugin_id()
    {
        var action = CommandLineParser.Parse(["--page", "plugin:com.typewhisper.openai"]);

        Assert.Equal(CliActionKind.LaunchGui, action.Kind);
        Assert.Equal("com.typewhisper.openai", action.Page?.PluginId);
    }

    [Theory]
    [InlineData("missing page name after --page", "--page")]
    [InlineData("missing page name after --page", "--page=")]
    [InlineData("missing page name after --page", "--page", "--minimized")]
    [InlineData("unknown settings page 'nope'", "--page", "nope")]
    [InlineData("unknown settings page 'plugin:a/b'", "--page", "plugin:a/b")]
    [InlineData("--page may be given only once", "--page", "general", "--page", "about")]
    [InlineData("--page may be given only once", "--page=general", "--page=general")]
    [InlineData("--page cannot be combined with --minimized", "--minimized", "--page", "general")]
    [InlineData("--page cannot be combined with 'status'", "--page", "general", "status")]
    [InlineData("--page cannot be combined with 'record'", "record", "start", "--page", "general")]
    [InlineData("--page cannot be combined with 'status'", "status", "--page=general")]
    public void Invalid_page_arguments_are_rejected(string error, params string[] args)
    {
        var action = CommandLineParser.Parse(args);

        Assert.Equal(CliActionKind.Invalid, action.Kind);
        Assert.Equal(error, action.ErrorMessage);
    }

    [Theory]
    [InlineData("""{"v":1,"ok":true}""", null)]
    [InlineData("""{"v":1,"ok":false,"error":"unknown-page"}""", "unknown-page")]
    [InlineData("""{"v":1,"ok":false,"error":"unknown-command"}""", "unknown-command")]
    [InlineData("""{"v":1,"ok":false}""", "malformed-reply")]
    [InlineData("""{"v":1,"ok":false,"error":null}""", "malformed-reply")]
    [InlineData("[]", "malformed-reply")]
    [InlineData("not json", "malformed-reply")]
    public void Hand_off_replies_are_read_as_success_or_an_error_code(string reply, string? expected)
    {
        Assert.Equal(expected, SettingsPageCommand.ReadError(reply));
    }

    [Fact]
    public void Hand_off_to_an_instance_that_never_listens_fails_after_the_window()
    {
        var exitCode = SettingsPageCommand.HandOffToStartingInstance(
            Path.Join(_tempDir, "missing.sock"),
            SettingsPage.TryParse("general")!,
            TimeSpan.FromMilliseconds(250)
        );

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Reveal_opens_an_enabled_plugin_with_its_settings()
    {
        var vm = CreateVm(("com.test.settings", true, true), ("com.test.other", true, true));

        var result = await vm.RevealPluginAsync("com.test.settings", Task.CompletedTask, Task.CompletedTask);

        Assert.Equal(PluginRevealResult.Revealed, result);
        Assert.Equal(0, vm.SelectedTab);
        var row = FindRow(vm, "com.test.settings");
        Assert.True(row.IsExpanded);
        Assert.Same(row, vm.TakeRevealedPlugin());
        Assert.Null(vm.TakeRevealedPlugin());
    }

    [Fact]
    public async Task Revealed_plugin_resolves_to_the_row_a_later_refresh_built()
    {
        var vm = CreateVm(("com.test.a", true, false));
        await vm.RevealPluginAsync("com.test.a", Task.CompletedTask, Task.CompletedTask);
        var revealedRow = FindRow(vm, "com.test.a");

        typeof(PluginsSectionViewModel)
            .GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null);

        var currentRow = FindRow(vm, "com.test.a");
        Assert.NotSame(revealedRow, currentRow);
        Assert.Same(currentRow, vm.TakeRevealedPlugin());
    }

    [Fact]
    public async Task Reveal_switches_to_the_disabled_tab_and_leaves_plugins_without_settings_closed()
    {
        var vm = CreateVm(("com.test.enabled", true, false), ("com.test.disabled", false, false));

        var result = await vm.RevealPluginAsync("com.test.disabled", Task.CompletedTask, Task.CompletedTask);

        Assert.Equal(PluginRevealResult.Revealed, result);
        Assert.Equal(1, vm.SelectedTab);
        Assert.False(FindRow(vm, "com.test.disabled").IsExpanded);
    }

    [Fact]
    public async Task Reveal_reports_a_plugin_that_is_not_installed()
    {
        var vm = CreateVm(("com.test.a", true, false));

        var result = await vm.RevealPluginAsync("com.test.missing", Task.CompletedTask, Task.CompletedTask);

        Assert.Equal(PluginRevealResult.NotInstalled, result);
        Assert.Equal(0, vm.SelectedTab);
        Assert.Null(vm.TakeRevealedPlugin());
    }

    [Fact]
    public async Task Reveal_looks_again_after_first_run_installs_settle()
    {
        var manager = TestPluginManagerFactory.Create(loadedPlugins: []);
        var vm = new PluginsSectionViewModel(manager, null, TimeSpan.FromSeconds(5), postToUi: action => action());
        var pluginsSettled = new TaskCompletionSource();

        var reveal = vm.RevealPluginAsync("com.test.late", Task.CompletedTask, pluginsSettled.Task);
        Assert.False(reveal.IsCompleted);

        // Stands in for the first-run registry install finishing after plugins loaded.
        var installed = (List<LoadedPlugin>)typeof(PluginManager)
            .GetField("_allPlugins", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        installed.Add(
            TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, "com.test.late", new FakePlugin("com.test.late"))
        );
        pluginsSettled.SetResult();

        Assert.Equal(PluginRevealResult.Revealed, await reveal);
    }

    [Fact]
    public async Task Plugin_ids_match_case_sensitively()
    {
        var vm = CreateVm(("com.test.a", true, false));

        Assert.Equal(
            PluginRevealResult.NotInstalled,
            await vm.RevealPluginAsync("COM.TEST.A", Task.CompletedTask, Task.CompletedTask)
        );
    }

    [Fact]
    public async Task Reveal_waits_for_plugins_and_yields_to_a_newer_navigation()
    {
        var vm = CreateVm(("com.test.a", true, true), ("com.test.b", false, true));
        var pluginsReady = new TaskCompletionSource();

        var first = vm.RevealPluginAsync("com.test.a", pluginsReady.Task, pluginsReady.Task);
        Assert.False(first.IsCompleted);

        var second = vm.RevealPluginAsync("com.test.b", pluginsReady.Task, pluginsReady.Task);
        pluginsReady.SetResult();

        Assert.Equal(PluginRevealResult.Superseded, await first);
        Assert.Equal(PluginRevealResult.Revealed, await second);
        Assert.Equal(1, vm.SelectedTab);
        Assert.False(FindRow(vm, "com.test.a").IsExpanded);
        Assert.True(FindRow(vm, "com.test.b").IsExpanded);
    }

    [Fact]
    public async Task Navigating_to_another_page_cancels_a_pending_reveal()
    {
        var vm = CreateVm(("com.test.missing-later", true, true));
        var pluginsReady = new TaskCompletionSource();

        var reveal = vm.RevealPluginAsync("com.test.not-installed", pluginsReady.Task, pluginsReady.Task);
        vm.CancelPendingReveal();
        pluginsReady.SetResult();

        Assert.Equal(PluginRevealResult.Superseded, await reveal);
    }

    private static PluginRow FindRow(PluginsSectionViewModel vm, string id) =>
        vm.EnabledGroups.Concat(vm.DisabledGroups)
            .SelectMany(group => group.Plugins)
            .First(row => row.Id == id);

    private PluginsSectionViewModel CreateVm(params (string Id, bool Enabled, bool HasSettings)[] plugins)
    {
        var loaded = plugins
            .Select(p =>
                TestPluginManagerFactory.CreateLoadedPlugin(
                    _tempDir,
                    p.Id,
                    p.HasSettings ? new FakeSettingsPlugin(p.Id) : new FakePlugin(p.Id)
                )
            )
            .ToList();
        var manager = TestPluginManagerFactory.Create(
            loadedPlugins: loaded,
            activatedPluginIds: plugins.Where(p => p.Enabled).Select(p => p.Id)
        );
        return new PluginsSectionViewModel(
            manager,
            null,
            TimeSpan.FromSeconds(5),
            postToUi: action => action()
        );
    }

    private class FakePlugin(string id) : ITypeWhisperPlugin
    {
        public string PluginId { get; } = id;
        public string PluginName => "Fake " + PluginId;
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeSettingsPlugin(string id) : FakePlugin(id), IPluginSettingsProvider
    {
        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions()
        {
            return [new PluginSettingDefinition("value", "Value", Kind: PluginSettingKind.Text)];
        }

        public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default)
        {
            return Task.FromResult<string?>("value");
        }

        public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
