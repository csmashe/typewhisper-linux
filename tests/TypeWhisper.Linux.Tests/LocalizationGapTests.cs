using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>Host text that used to stay English after the UI language changed.</summary>
public sealed class LocalizationGapTests : IDisposable
{
    private readonly string _originalLanguage = Loc.Instance.CurrentLanguage;
    private readonly string _dir = Path.Join(Path.GetTempPath(), "tw-locgap-" + Guid.NewGuid().ToString("N"));

    public LocalizationGapTests()
    {
        Directory.CreateDirectory(_dir);
        Loc.Instance.CurrentLanguage = "en";
    }

    public void Dispose()
    {
        Loc.Instance.CurrentLanguage = _originalLanguage;
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AutoUiLanguageOption_FollowsTheLanguage_WhileNativeNamesStay()
    {
        var auto = Loc.Instance.AvailableUiLanguages.Single(option => option.Code is null);
        var german = Loc.Instance.AvailableUiLanguages.Single(option => option.Code == "de");
        var changed = new List<string?>();
        auto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Loc.Instance.CurrentLanguage = "de";

        Assert.Equal("Automatisch (System)", auto.DisplayName);
        Assert.Equal("Deutsch", german.DisplayName);
        Assert.Contains(nameof(UiLanguageOption.DisplayName), changed);
    }

    [Fact]
    public void TranslationTargetSpecialChoices_AreLocalized()
    {
        Loc.Instance.CurrentLanguage = "de";

        Assert.Equal("Keine Übersetzung", TranslationTargetLabels.GlobalOptions().Single(o => o.Code is null).DisplayName);
        var profile = TranslationTargetLabels.ProfileOptions().ToList();
        Assert.Equal("Globale Einstellung verwenden", profile.Single(o => o.Code is null).DisplayName);
        Assert.Equal("Keine Übersetzung", profile.Single(o => o.Code == "").DisplayName);
    }

    [Fact]
    public void PluginFieldRelabel_KeepsTheValueAndRelabelsTheSelectedOption()
    {
        var field = new PluginSettingFieldRow(
            "mode",
            "Mode",
            "English description",
            string.Empty,
            [new PluginSettingOption("a", "Alpha"), new PluginSettingOption("b", "Beta")],
            false,
            PluginSettingKind.Dropdown,
            "b"
        );

        field.Relabel(
            new PluginSettingDefinition(
                "mode",
                "Modus",
                Description: "Deutsche Beschreibung",
                Options: [new PluginSettingOption("a", "Alfa"), new PluginSettingOption("b", "Beta (de)")]
            )
        );

        Assert.Equal("Modus", field.Label);
        Assert.Equal("Deutsche Beschreibung", field.Description);
        Assert.Equal("b", field.Value);
        Assert.Equal(new PluginSettingOption("b", "Beta (de)"), field.SelectedOption);
        Assert.Equal(["Alfa", "Beta (de)"], field.Options.Select(o => o.Label));
        Assert.False(field.IsUserModified);
    }

    [Fact]
    public void SeededNames_FollowTheLanguage_UnlessRenamed()
    {
        var path = Path.Join(_dir, "prompt-actions.json");
        var sut = new PromptActionService(path, names: LocDefaultNameLocalizer.Instance);
        sut.SeedFirstRunDefaultsIfMissing();
        sut.SeedPresets();
        var reply = sut.Actions.Single(a => a.Name == "Reply");
        sut.UpdateAction(reply with { Name = "My reply" });

        Loc.Instance.CurrentLanguage = "de";
        sut.LocalizeDefaultNames();

        Assert.Equal("Text automatisch bereinigen", sut.Actions.Single(a => a.Id == FirstRunDefaults.AutoCleanupActionId).Name);
        Assert.Contains(sut.Actions, a => a.Name == "Ins Englische übersetzen");
        Assert.Contains(sut.Actions, a => a.Name == "My reply");

        Loc.Instance.CurrentLanguage = "ja";
        sut.LocalizeDefaultNames();

        Assert.Contains(sut.Actions, a => a.Name == "英語に翻訳");
    }

    [Fact]
    public void LocalizingSeededNames_NeverCreatesTheFile()
    {
        var actionsPath = Path.Join(_dir, "prompt-actions.json");
        var profilesPath = Path.Join(_dir, "profiles.json");

        new PromptActionService(actionsPath, names: LocDefaultNameLocalizer.Instance).LocalizeDefaultNames();
        new ProfileService(profilesPath, names: LocDefaultNameLocalizer.Instance).LocalizeDefaultNames();

        Assert.False(File.Exists(actionsPath));
        Assert.False(File.Exists(profilesPath));
    }

    [Fact]
    public void SeededProfileName_FollowsTheLanguage()
    {
        Loc.Instance.CurrentLanguage = "es";
        var sut = new ProfileService(Path.Join(_dir, "profiles.json"), names: LocDefaultNameLocalizer.Instance);
        sut.SeedFirstRunDefaultsIfMissing();
        Assert.Equal("Formato automático", Assert.Single(sut.Profiles).Name);

        Loc.Instance.CurrentLanguage = "ru";
        sut.LocalizeDefaultNames();

        Assert.Equal("Автоформатирование", Assert.Single(sut.Profiles).Name);
    }
}
