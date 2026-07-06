using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Verifies first-run seeding of the default cleanup prompt action and auto-format profile is correct and idempotent.</summary>
public sealed class FirstRunSeedingTests : IDisposable
{
    private readonly string _dir;

    public FirstRunSeedingTests()
    {
        _dir = Path.Join(Path.GetTempPath(), "tw-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string FreshPath(string name) => Path.Join(_dir, name);

    [Fact]
    public void PromptAction_SeedsDisabledCleanupActionOnFirstRun()
    {
        var path = FreshPath("prompt-actions.json");
        var sut = new PromptActionService(path);

        sut.SeedFirstRunDefaultsIfMissing();

        var action = Assert.Single(sut.Actions);
        Assert.Equal(FirstRunDefaults.AutoCleanupActionId, action.Id);
        Assert.Equal("Auto Clean Up Text", action.Name);
        Assert.False(action.IsEnabled);
        Assert.Null(action.ProviderOverride);
        Assert.Equal(FirstRunDefaults.AutoCleanupSystemPrompt, action.SystemPrompt);

        // Persists so a fresh service instance sees the same seeded action.
        var reloaded = new PromptActionService(path);
        Assert.Single(reloaded.Actions);
    }

    [Fact]
    public void PromptAction_DoesNotSeedWhenFileAlreadyExists()
    {
        var path = FreshPath("prompt-actions.json");
        File.WriteAllText(path, "[]");

        var sut = new PromptActionService(path);
        sut.SeedFirstRunDefaultsIfMissing();

        Assert.Empty(sut.Actions);
    }

    [Fact]
    public void PromptAction_SeedIsIdempotentAcrossInstancesOnSameFile()
    {
        var path = FreshPath("prompt-actions.json");

        new PromptActionService(path).SeedFirstRunDefaultsIfMissing();
        // Second instance: file now exists, so it must not add a duplicate.
        var second = new PromptActionService(path);
        second.SeedFirstRunDefaultsIfMissing();

        Assert.Single(second.Actions);
    }

    [Fact]
    public void Profile_SeedsDisabledAutoFormatProfileWiredToCleanupAction()
    {
        var path = FreshPath("profiles.json");
        var sut = new ProfileService(path);

        sut.SeedFirstRunDefaultsIfMissing();

        var profile = Assert.Single(sut.Profiles);
        Assert.Equal(FirstRunDefaults.AutoFormatProfileId, profile.Id);
        Assert.Equal("Auto Format", profile.Name);
        Assert.False(profile.IsEnabled);
        Assert.Equal(FirstRunDefaults.AutoCleanupActionId, profile.PromptActionId);
        Assert.Equal("Ctrl + Alt + E", profile.HotkeyData);
        Assert.Equal(ProfileHotkeyBehavior.StartDictation, profile.HotkeyBehavior);
    }

    [Fact]
    public void Profile_DoesNotSeedWhenFileAlreadyExists()
    {
        var path = FreshPath("profiles.json");
        File.WriteAllText(path, "[]");

        var sut = new ProfileService(path);
        sut.SeedFirstRunDefaultsIfMissing();

        Assert.Empty(sut.Profiles);
    }
}
