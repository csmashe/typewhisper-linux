using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class DefaultNameLocalizationTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "tw-default-names-" + Guid.NewGuid().ToString("N"));
    private readonly TestNames _names = new();

    public DefaultNameLocalizationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("Reply")]
    [InlineData("Antwort")]
    [InlineData("E-Mail schreiben")]
    public void ExplicitPresetRename_ToAnyShippedName_SurvivesLanguageChangesAndReload(string chosenName)
    {
        var path = Path.Join(_directory, "actions.json");
        var service = new PromptActionService(path, names: _names);
        service.SeedPresets();
        var email = service.Actions.Single(a => a.DefaultNameKey == DefaultItemNames.WriteEmail);

        service.UpdateAction(email with { Name = chosenName });
        _names.Language = "de";
        service.LocalizeDefaultNames();
        Assert.Equal(chosenName, service.Actions.Single(a => a.Id == email.Id).Name);

        var reloaded = new PromptActionService(path, names: _names);
        _names.Language = "en";
        reloaded.LocalizeDefaultNames();
        var renamed = reloaded.Actions.Single(a => a.Id == email.Id);
        Assert.Equal(chosenName, renamed.Name);
        Assert.True(renamed.IsNameCustomized);
        Assert.Equal(DefaultItemNames.WriteEmail, renamed.DefaultNameKey);
        Assert.Equal(email.SystemPrompt, renamed.SystemPrompt);
    }

    [Theory]
    [InlineData("Automatisch formatieren")]
    [InlineData("My profile")]
    public void ExplicitSeededProfileRename_SurvivesLanguageChangesAndReload(string chosenName)
    {
        var path = Path.Join(_directory, "profiles.json");
        var service = new ProfileService(path, names: _names);
        service.SeedFirstRunDefaultsIfMissing();
        var profile = Assert.Single(service.Profiles);
        service.UpdateProfile(profile with { Name = chosenName });

        _names.Language = "de";
        service.LocalizeDefaultNames();
        var reloaded = new ProfileService(path, names: _names);
        _names.Language = "en";
        reloaded.LocalizeDefaultNames();

        Assert.Equal(chosenName, Assert.Single(reloaded.Profiles).Name);
        Assert.True(Assert.Single(reloaded.Profiles).IsNameCustomized);
    }

    [Fact]
    public void SavingOtherPresetFields_KeepsStableIdentityAndLanguageFollowing()
    {
        var path = Path.Join(_directory, "actions.json");
        var service = new PromptActionService(path, names: _names);
        service.SeedPresets();
        var email = service.Actions.Single(a => a.DefaultNameKey == DefaultItemNames.WriteEmail);

        // Older clients may omit the new metadata when updating a record.
        service.UpdateAction(new PromptAction
        {
            Id = email.Id,
            Name = email.Name,
            SystemPrompt = "Custom email instructions",
            IsPreset = true,
        });
        _names.Language = "de";
        service.LocalizeDefaultNames();

        var updated = new PromptActionService(path, names: _names).Actions.Single(a => a.Id == email.Id);
        Assert.Equal("E-Mail schreiben", updated.Name);
        Assert.Equal(DefaultItemNames.WriteEmail, updated.DefaultNameKey);
        Assert.False(updated.IsNameCustomized);
        Assert.Equal("Custom email instructions", updated.SystemPrompt);
    }

    [Fact]
    public void OlderClientUpdates_CannotClearPersistedRenameIntent()
    {
        var actionPath = Path.Join(_directory, "actions.json");
        var actions = new PromptActionService(actionPath, names: _names);
        actions.SeedFirstRunDefaultsIfMissing();
        var action = Assert.Single(actions.Actions);
        actions.UpdateAction(action with { Name = "Text automatisch bereinigen" });
        actions.UpdateAction(new PromptAction
        {
            Id = action.Id,
            Name = "Text automatisch bereinigen",
            SystemPrompt = action.SystemPrompt,
        });

        var profilePath = Path.Join(_directory, "profiles.json");
        var profiles = new ProfileService(profilePath, names: _names);
        profiles.SeedFirstRunDefaultsIfMissing();
        var profile = Assert.Single(profiles.Profiles);
        profiles.UpdateProfile(profile with { Name = "Automatisch formatieren" });
        profiles.UpdateProfile(new Profile { Id = profile.Id, Name = "Automatisch formatieren", Priority = 7 });

        actions.LocalizeDefaultNames();
        profiles.LocalizeDefaultNames();
        Assert.Equal("Text automatisch bereinigen", Assert.Single(actions.Actions).Name);
        Assert.True(Assert.Single(actions.Actions).IsNameCustomized);
        Assert.Equal("Automatisch formatieren", Assert.Single(profiles.Profiles).Name);
        Assert.True(Assert.Single(profiles.Profiles).IsNameCustomized);
    }

    [Fact]
    public void UntouchedDefaults_ContinueFollowingLanguageAfterReload()
    {
        var actionPath = Path.Join(_directory, "actions.json");
        var actions = new PromptActionService(actionPath, names: _names);
        actions.SeedFirstRunDefaultsIfMissing();
        actions.SeedPresets();
        var profilePath = Path.Join(_directory, "profiles.json");
        var profiles = new ProfileService(profilePath, names: _names);
        profiles.SeedFirstRunDefaultsIfMissing();
        var profile = Assert.Single(profiles.Profiles);
        profiles.UpdateProfile(profile with { Priority = 4 });

        _names.Language = "de";
        actions.LocalizeDefaultNames();
        profiles.LocalizeDefaultNames();
        Assert.Equal("Text automatisch bereinigen", actions.Actions.Single(a => a.Id == FirstRunDefaults.AutoCleanupActionId).Name);
        Assert.Equal("Automatisch formatieren", Assert.Single(profiles.Profiles).Name);
        Assert.False(Assert.Single(profiles.Profiles).IsNameCustomized);

        _names.Language = "en";
        var reloadedActions = new PromptActionService(actionPath, names: _names);
        var reloadedProfiles = new ProfileService(profilePath, names: _names);
        reloadedActions.LocalizeDefaultNames();
        reloadedProfiles.LocalizeDefaultNames();
        Assert.Equal("Auto Clean Up Text", reloadedActions.Actions.Single(a => a.Id == FirstRunDefaults.AutoCleanupActionId).Name);
        Assert.Equal("Auto Format", Assert.Single(reloadedProfiles.Profiles).Name);
        Assert.Equal(4, Assert.Single(reloadedProfiles.Profiles).Priority);
    }

    [Fact]
    public void LegacyPresets_MigrateOnlyWhenPromptAndOwnShippedNameAgree()
    {
        var seed = new PromptActionService(Path.Join(_directory, "seed.json"));
        seed.SeedPresets();
        var email = seed.Actions.Single(a => a.Name == "Write Email");
        var reply = seed.Actions.Single(a => a.Name == "Reply");
        var list = seed.Actions.Single(a => a.Name == "Format as List");
        var legacy = new[]
        {
            email with { Id = "renamed-email", Name = "Reply" },
            reply with { Id = "untouched-reply" },
            list with { Id = "edited-list", SystemPrompt = "My custom list prompt" },
        };
        var path = Path.Join(_directory, "legacy.json");
        File.WriteAllText(path, JsonSerializer.Serialize(legacy.Select(a => new { a.Id, a.Name, a.SystemPrompt, a.IsPreset })));

        var service = new PromptActionService(path, names: _names);
        _names.Language = "de";
        service.LocalizeDefaultNames();

        Assert.Equal("Reply", service.Actions.Single(a => a.Id == "renamed-email").Name);
        Assert.Null(service.Actions.Single(a => a.Id == "renamed-email").DefaultNameKey);
        Assert.Equal("Antwort", service.Actions.Single(a => a.Id == "untouched-reply").Name);
        Assert.Equal(DefaultItemNames.Reply, service.Actions.Single(a => a.Id == "untouched-reply").DefaultNameKey);
        Assert.Equal("Format as List", service.Actions.Single(a => a.Id == "edited-list").Name);
    }

    [Fact]
    public void UserAuthoredActions_AreNotTreatedAsPresetsByTheirNameOrPrompt()
    {
        var seed = new PromptActionService(Path.Join(_directory, "seed.json"));
        seed.SeedPresets();
        var reply = seed.Actions.Single(a => a.Name == "Reply");
        var service = new PromptActionService(Path.Join(_directory, "actions.json"), names: _names);
        service.AddAction(reply with { Id = "user", IsPreset = false, DefaultNameKey = null });

        _names.Language = "de";
        service.LocalizeDefaultNames();

        Assert.Equal("Reply", Assert.Single(service.Actions).Name);
    }

    private sealed class TestNames : IDefaultNameLocalizer
    {
        public string Language { get; set; } = "en";

        public string GetName(string key) => Language == "de" ? GermanName(key) : DefaultItemNames.English.GetName(key);

        public bool IsShippedName(string key, string name) =>
            name == DefaultItemNames.English.GetName(key) || name == GermanName(key);

        private static string GermanName(string key) => key switch
        {
            DefaultItemNames.AutoCleanupAction => "Text automatisch bereinigen",
            DefaultItemNames.AutoFormatProfile => "Automatisch formatieren",
            DefaultItemNames.WriteEmail => "E-Mail schreiben",
            DefaultItemNames.Reply => "Antwort",
            _ => "DE-" + DefaultItemNames.English.GetName(key),
        };
    }
}
