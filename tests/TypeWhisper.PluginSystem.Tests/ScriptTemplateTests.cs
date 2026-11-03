using System.Runtime.CompilerServices;
using System.Text.Json;
using Moq;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Built-in templates through real bash, and draft tests that leave the saved scripts alone.
/// </summary>
public sealed class ScriptTemplateTests : IDisposable
{
    private const string CollectionKey = "scripts";

    private readonly string _directory = TestPaths.CreateTempDirectory("script-template");

    public static TheoryData<string, string, string> TemplateCases =>
        new()
        {
            { "uppercase", "Äpfel und Öl, straße\n", "ÄPFEL UND ÖL, STRAßE\n" },
            { "lowercase", "ÄPFEL UND ÖL\n", "äpfel und öl\n" },
            { "trim", " \t Hallo Welt! \n\n", "Hallo Welt!" },
            { "clean-spacing", "  a   b\t\tc  \r\n\n  d  é \n", "a b c\n\nd é" },
            { "bullets", "eins\n\n- zwei\n  drei  \n* vier", "- eins\n- zwei\n- drei\n* vier" },
            {
                "checklist",
                "eins\n- zwei\n- [x] drei\n* [ ] vier\n\n",
                "- [ ] eins\n- [ ] zwei\n- [x] drei\n* [ ] vier"
            },
            { "quote", "eins\n\n> zwei\n", "> eins\n> \n> zwei" },
        };

    [Theory]
    [MemberData(nameof(TemplateCases))]
    public async Task Template_TransformsStdin(string templateId, string input, string expected)
    {
        var result = await TestTemplateAsync(templateId, input);

        Assert.True(result.IsSuccess, result.Message + result.Output);
        Assert.Equal(expected, result.Output);
    }

    [Theory]
    [InlineData("Sag \"hi\" \\ dann\nÄ\tx\u0001\r\b\f")]
    [InlineData("")]
    public async Task JsonTemplate_RoundTripsThroughAJsonParser(string input)
    {
        var result = await TestTemplateAsync("json", input);

        Assert.True(result.IsSuccess, result.Message + result.Output);
        Assert.Equal(input, JsonSerializer.Deserialize<string>(result.Output!));
    }

    [Theory]
    [InlineData("a b/ä~-_.?&=😀")]
    [InlineData("plain")]
    public async Task UrlTemplate_MatchesEscapeDataString(string input)
    {
        var result = await TestTemplateAsync("url", input);

        Assert.True(result.IsSuccess, result.Message + result.Output);
        Assert.Equal(Uri.EscapeDataString(input), result.Output);
    }

    [Fact]
    public void Templates_AreDisabledBashCopiesWithEveryIdLocalized()
    {
        var plugin = new ScriptPlugin { IsExecutableInstalled = _ => true };
        plugin.SetLocalization(new PluginLocalization(PluginDirectory()));

        var templates = Assert.Single(plugin.GetCollectionDefinitions()).Templates;

        Assert.Equal(ScriptTemplates.All.Select(t => t.Id), templates.Select(t => t.Id));
        Assert.All(
            templates,
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the lambda exists to assert on each template; checking is its whole job.
            template =>
            {
                Assert.Equal("false", template.Values["enabled"]);
                Assert.Equal("bash", template.Values["shell"]);
                Assert.Equal(template.Label, template.Values["name"]);
                Assert.DoesNotContain("Templates.", template.Label, StringComparison.Ordinal);
                Assert.DoesNotContain("Templates.", template.Description, StringComparison.Ordinal);
            }
        );
    }

    [Fact]
    public void Templates_AreHiddenWithoutBash()
    {
        var plugin = new ScriptPlugin { IsExecutableInstalled = _ => false };

        var definition = Assert.Single(plugin.GetCollectionDefinitions());

        Assert.Empty(definition.Templates);
        Assert.NotNull(definition.ItemTest);
    }

    [Fact]
    public void IsInstalled_FindsBashButNotAMissingName()
    {
        Assert.True(ScriptShells.IsInstalled("bash"));
        Assert.False(ScriptShells.IsInstalled("typewhisper-no-such-shell-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task TestItem_RunsTheEditedDraft_WithoutSavingIt()
    {
        var plugin = await ActivateAsync();
        var id = Guid.NewGuid().ToString("D");
        Assert.True(
            (await plugin.SetItemsAsync(CollectionKey, [Item("Saved", "printf saved", id: id)])).IsSuccess
        );
        var savedJson = await File.ReadAllTextAsync(ConfigPath);

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("", "tr a-z A-Z", enabled: "false", id: id),
            "draft",
            CancellationToken.None
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("DRAFT", result.Output);
        Assert.Equal(savedJson, await File.ReadAllTextAsync(ConfigPath));
        var saved = Assert.Single(plugin.Service!.Scripts);
        Assert.Equal("printf saved", saved.Command);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public async Task TestItem_PassesUnicodeThroughUnchanged()
    {
        var plugin = await ActivateAsync();
        const string input = "Grüße, naïve café — 日本語 😀\n";

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("Echo", "cat", shell: "sh"),
            input,
            CancellationToken.None
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(input, result.Output);
    }

    [Fact]
    public async Task TestItem_NonZeroExit_FailsWithStandardError()
    {
        var plugin = await ActivateAsync();

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("Broken", "echo 'bad input' >&2; exit 3"),
            "x",
            CancellationToken.None
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("3", result.Message, StringComparison.Ordinal);
        Assert.Equal("bad input", result.Output);
    }

    [Fact]
    public async Task TestItem_TimesOutAtTheDictationLimit()
    {
        var plugin = await ActivateAsync();

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("Slow", "sleep 30"),
            "x",
            CancellationToken.None
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("5", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestItem_BoundsCapturedOutput()
    {
        var plugin = await ActivateAsync();

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("Flood", "head -c 4000000 /dev/zero | tr '\\0' x"),
            "x",
            CancellationToken.None
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("1024", result.Message, StringComparison.Ordinal);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task TestItem_TruncatesALongPreview()
    {
        var plugin = await ActivateAsync();

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("Long", "head -c 10000 /dev/zero | tr '\\0' y"),
            "x",
            CancellationToken.None
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new string('y', 4000) + "…", result.Output);
    }

    [Fact]
    public async Task TestItem_CallerCancellation_ThrowsAndStopsTheScript()
    {
        var plugin = await ActivateAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.TestItemAsync(CollectionKey, Item("Slow", "sleep 30"), "x", cts.Token)
        );

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData("", "bash", "command is required")]
    [InlineData("echo x", "cmd", "unknown shell 'cmd'")]
    public async Task TestItem_InvalidDraft_FailsWithoutRunning(
        string command,
        string shell,
        string expected
    )
    {
        var plugin = await ActivateAsync();

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("Draft", command, shell: shell),
            "x",
            CancellationToken.None
        );

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task TestItem_NotActivated_AsksToEnableThePlugin()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_directory);

        var result = await plugin.TestItemAsync(
            CollectionKey,
            Item("Draft", "cat"),
            "x",
            CancellationToken.None
        );

        Assert.False(result.IsSuccess);
        Assert.Equal("Settings.TestRequiresEnabled", result.Message);
    }

    [Fact]
    public void Localization_EveryLocaleHasEveryEnglishKey()
    {
        var directory = Path.Join(PluginDirectory(), "Localization");
        var english = Keys(Path.Join(directory, "en.json"));
        foreach (var locale in new[] { "de", "es", "ru" })
        {
            Assert.Equal(english, Keys(Path.Join(directory, locale + ".json")));
        }
    }

    private static string[] Keys(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_directory);
    }

    private string ConfigPath => Path.Join(_directory, "scripts.json");

    private async Task<PluginCollectionItemTestResult> TestTemplateAsync(string templateId, string input)
    {
        var plugin = await ActivateAsync();
        var template = Assert
            .Single(plugin.GetCollectionDefinitions())
            .Templates.Single(t => t.Id == templateId);
        return await plugin.TestItemAsync(
            CollectionKey,
            new PluginCollectionItem(template.Values),
            input,
            CancellationToken.None
        );
    }

    private async Task<ScriptPlugin> ActivateAsync()
    {
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(value => value.PluginDataDirectory).Returns(_directory);
        host.SetupGet(value => value.Processes).Returns(new ProcessRunner());
        host.SetupGet(value => value.Localization).Returns(new PluginLocalization(PluginDirectory(), "en"));
        var plugin = new ScriptPlugin { IsExecutableInstalled = _ => true };
        plugin.SetDataDirectory(_directory);
        await plugin.ActivateAsync(host.Object);
        return plugin;
    }

    private static string PluginDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(
            Path.Join(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins", "TypeWhisper.Plugin.Script")
        );

    private static PluginCollectionItem Item(
        string name,
        string command,
        string shell = "bash",
        string enabled = "true",
        string? id = null
    )
    {
        var values = new Dictionary<string, string?>
        {
            ["name"] = name,
            ["command"] = command,
            ["shell"] = shell,
            ["enabled"] = enabled,
        };
        if (id is not null)
            values["__id"] = id;

        return new PluginCollectionItem(values);
    }
}
