using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using Xunit;

namespace TypeWhisper.UiTests;

// ReSharper disable once ClassNeverInstantiated.Global -- created by xunit as the "ui" collection fixture.
public sealed class UiSession : IDisposable
{
    public UiSession()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(UiTestApp));
        Services = Session.Dispatch(UiServices.Build, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal HeadlessUnitTestSession Session { get; }

    internal ServiceProvider Services { get; }

    public void Dispose()
    {
        Session.Dispatch(() => Services.Dispose(), CancellationToken.None).GetAwaiter().GetResult();
        Session.Dispose();
    }
}

[CollectionDefinition("ui")]
public sealed class UiCollection : ICollectionFixture<UiSession>;

/// <summary>
///     Renders every Settings section in the real main window with real ViewModels, in each
///     theme, window size, and UI language, saves a PNG of each, and fails on text a person
///     cannot read or controls a screen reader cannot name.
/// </summary>
[Collection("ui")]
public sealed class MainWindowRenderTests(UiSession ui)
{
    private static readonly string[] s_languages = ["en", "de", "es", "ru", "ja", "zh"];

    public static TheoryData<string> Languages => new(s_languages);

    internal static readonly (string Name, double Width, double Height)[] s_sizes =
        [("wide", 1200, 780), ("compact", 960, 640)];

    // Every window pins RequestedThemeVariant="Dark"; add Light here when the app supports it.
    private static readonly (string Name, ThemeVariant Variant)[] s_themes = [("dark", ThemeVariant.Dark)];

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task EverySection_RendersReadableAndNamedInEveryThemeAndSize(string language)
    {
        // Without a CJK font every glyph renders as a fallback box, so clipping is measured wrongly.
        if (language is "ja" or "zh")
        {
            Assert.True(
                await ui.Session.Dispatch(HasCjkFont, CancellationToken.None),
                "Rendering ja/zh needs a CJK font, for example fonts-noto-cjk.");
        }

        var findings = await ui.Session.Dispatch(() => RenderAll(language), CancellationToken.None);

        var outFile = Path.Join(UiEnvironment.SnapshotDir, $"findings-{language}.json");
        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(findings, s_indented));
        // Report every kind together so one category cannot hide another.
        var report = string.Join("\n\n", new[] { UiInspector.RawKey, UiInspector.ClippedText, UiInspector.UnnamedControl, UiInspector.CodeLikeName, UiInspector.WrongLanguage }
            .Select(kind => Describe(findings, kind, language, outFile))
            .Where(message => message is not null));
        Assert.True(report.Length == 0, report);
    }

    private List<RenderedFinding> RenderAll(string language)
    {
        Loc.Instance.CurrentLanguage = language;
        try
        {
            var knownKeys = KnownKeys();
            var foreignText = ForeignText(language);
            var findings = new List<RenderedFinding>();
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            foreach (var (themeName, theme) in s_themes)
            {
                Application.Current!.RequestedThemeVariant = theme;
                foreach (var (sizeName, width, height) in s_sizes)
                {
                    var window = new MainWindow(viewModel) { Width = width, Height = height };
                    window.Show();
                    try
                    {
                        foreach (var item in viewModel.NavItems.Where(item => item.Content is not null))
                        {
                            viewModel.SelectedItem = item;
                            Settle();
                            var section = item.Content!.GetType().Name.Replace("SectionViewModel", "", StringComparison.Ordinal);
                            var variant = $"{section}-{language}-{themeName}-{sizeName}";
                            window.CaptureRenderedFrame()?.Save(Path.Join(UiEnvironment.SnapshotDir, variant + ".png"), PngBitmapEncoderOptions.Default);
                            findings.AddRange(UiInspector.Inspect(window, knownKeys, foreignText)
                                .Distinct()
                                .Select(finding => new RenderedFinding(section, language, themeName, sizeName, finding)));
                        }
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            return findings;
        }
        finally
        {
            Loc.Instance.CurrentLanguage = "en";
        }
    }

    private static bool HasCjkFont() =>
        FontManager.Current.TryMatchCharacter('日', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
            null, CultureInfo.GetCultureInfo("ja"), out _);

    internal static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static HashSet<string> KnownKeys()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Join(UiEnvironment.LocalizationDir, "en.json")));
        return catalog.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Catalog(string language)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Join(UiEnvironment.LocalizationDir, language + ".json")));
        return catalog.RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    /// <summary>Text that only another UI language uses. Short words are shared across languages, so skipped.</summary>
    private static HashSet<string> ForeignText(string language)
    {
        var allowed = Catalog(language).Values.ToHashSet(StringComparer.Ordinal);
        return s_languages.Where(other => other != language)
            .SelectMany(other => Catalog(other).Values)
            .Where(text => text.Length > 3 && !text.Contains('{') && !allowed.Contains(text))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? Describe(List<RenderedFinding> findings, string kind, string language, string file)
    {
        // A problem in one view usually repeats across themes and sizes; report each once.
        var distinct = findings.Where(f => f.Finding.Kind == kind)
            .GroupBy(f => (f.Section, f.Finding.Control, f.Finding.Detail, f.Finding.Location))
            .Select(group => $"{group.Key.Section}: {group.Key.Control} {group.Key.Detail} at {group.Key.Location} "
                             + $"[{string.Join(", ", group.Select(f => $"{f.Theme}/{f.Size}").Distinct())}]")
            .ToList();
        return distinct.Count == 0
            ? null
            : $"{distinct.Count} {kind} finding(s) in '{language}' (full list: {file}):\n" + string.Join("\n", distinct.Take(40));
    }

    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    // ReSharper disable once NotAccessedPositionalProperty.Local -- Language is read from the serialized findings-<lang>.json.
    private sealed record RenderedFinding(string Section, string Language, string Theme, string Size, UiFinding Finding);
}
