using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class ProfileServiceTests : IDisposable
{
    private readonly string _filePath;
    private readonly ProfileService _sut;

    public ProfileServiceTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new ProfileService(_filePath);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    [Fact]
    public void PromptActionId_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Profile",
            PromptActionId = "prompt-123"
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("prompt-123", loaded.PromptActionId);
    }

    [Fact]
    public void PromptActionId_NullRoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "No Prompt",
            PromptActionId = null
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Null(loaded.PromptActionId);
    }

    [Fact]
    public void UpdateProfile_ChangesPromptActionId()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test",
            PromptActionId = null
        };

        _sut.AddProfile(profile);
        _sut.UpdateProfile(profile with { PromptActionId = "action-456" });

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("action-456", loaded.PromptActionId);
    }

    [Fact]
    public void HotkeyData_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "With Hotkey",
            HotkeyData = "{\"key\":\"Ctrl+1\"}"
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal("{\"key\":\"Ctrl+1\"}", loaded.HotkeyData);
    }

    [Fact]
    public void HotkeyData_NullByDefault()
    {
        var profile = new Profile { Id = Guid.NewGuid().ToString(), Name = "No Hotkey" };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Null(loaded.HotkeyData);
    }

    [Fact]
    public void StylePreset_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Email",
            StylePreset = ProfileStylePreset.FormalEmail
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal(ProfileStylePreset.FormalEmail, loaded.StylePreset);
    }

    [Fact]
    public void StylePreset_DefaultsToRawForLegacyJson()
    {
        File.WriteAllText(
            _filePath,
            """
            [
              {
                "Id": "legacy",
                "Name": "Legacy"
              }
            ]
            """
        );

        var freshService = new ProfileService(_filePath);

        var loaded = Assert.Single(freshService.Profiles);
        Assert.Equal(ProfileStylePreset.Raw, loaded.StylePreset);
    }

    [Fact]
    public void StylePresetService_ResolvesDeveloperWithoutTerminalSafe()
    {
        var result = ProfileStylePresetService.Resolve(ProfileStylePreset.Developer);

        Assert.Equal(CleanupLevel.None, result.CleanupLevel);
        Assert.True(result.DeveloperFormattingEnabled);
        Assert.False(result.TerminalSafe);
    }

    [Fact]
    public void StylePresetService_ResolvesCleanWithLightCleanup()
    {
        var result = ProfileStylePresetService.Resolve(ProfileStylePreset.Clean);

        Assert.Equal(CleanupLevel.Light, result.CleanupLevel);
        Assert.True(result.SmartFormattingEnabled);
        Assert.False(result.TerminalSafe);
    }

    [Fact]
    public void HotkeyBehavior_RoundTrips()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Selection",
            HotkeyData = "Ctrl+Shift+S",
            HotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText
        };

        _sut.AddProfile(profile);

        var freshService = new ProfileService(_filePath);
        var loaded = freshService.Profiles.First(p => p.Id == profile.Id);
        Assert.Equal(ProfileHotkeyBehavior.ProcessSelectedText, loaded.HotkeyBehavior);
    }

    [Fact]
    public void HotkeyBehavior_DefaultsToStartDictationForLegacyJson()
    {
        File.WriteAllText(
            _filePath,
            """
            [
              {
                "Id": "legacy",
                "Name": "Legacy"
              }
            ]
            """
        );

        var freshService = new ProfileService(_filePath);

        var loaded = Assert.Single(freshService.Profiles);
        Assert.Equal(ProfileHotkeyBehavior.StartDictation, loaded.HotkeyBehavior);
    }

    [Fact]
    public void MatchProfile_ForcedEnabledProfile_ReturnsManualOverride()
    {
        var forced = new Profile
        {
            Id = "forced",
            Name = "Forced",
            ProcessNames = ["never-matches"]
        };
        _sut.AddProfile(forced);

        // No process/url context matches the forced profile, yet the forced id
        // wins as a ManualOverride.
        var result = _sut.MatchProfile("some-other-app", null, "forced");

        Assert.Equal(MatchKind.ManualOverride, result.Kind);
        Assert.NotNull(result.Profile);
        Assert.Equal("forced", result.Profile!.Id);
    }

    [Fact]
    public void MatchProfile_ForcedDisabledProfile_FallsThrough()
    {
        // A normal enabled profile with no matchers acts as the global
        // fallback; the forced id pointing at a disabled profile must fall
        // through to it rather than honoring the disabled selection.
        _sut.AddProfile(new Profile { Id = "fallback", Name = "Fallback" });
        _sut.AddProfile(new Profile
        {
            Id = "forced",
            Name = "Forced",
            IsEnabled = false
        });

        var result = _sut.MatchProfile(null, null, "forced");

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.NotNull(result.Profile);
        Assert.Equal("fallback", result.Profile!.Id);
    }

    [Fact]
    public void MatchProfile_ForcedMissingProfile_FallsThrough()
    {
        _sut.AddProfile(new Profile { Id = "fallback", Name = "Fallback" });

        var result = _sut.MatchProfile(null, null, "does-not-exist");

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.NotNull(result.Profile);
        Assert.Equal("fallback", result.Profile!.Id);
    }

    [Fact]
    public void MatchProfile_HotkeyOnlyProfileWithNoMatchers_IsExcludedFromGlobalFallback()
    {
        // A profile with no app/URL matchers but a hotkey is hotkey-only: it
        // must NOT act as the global fallback, or it would hijack plain
        // dictation in every window.
        _sut.AddProfile(new Profile
        {
            Id = "hotkey-only",
            Name = "Hotkey Only",
            HotkeyData = "Ctrl+Alt+E"
        });

        var result = _sut.MatchProfile("some-app", null);

        Assert.Equal(MatchKind.NoMatch, result.Kind);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void MatchProfile_HotkeyOnlyProfile_StillForceMatchesByHotkey()
    {
        // The exclusion only applies to the ambient cascade; forcing the
        // profile by id (its chord was pressed) must still win.
        _sut.AddProfile(new Profile
        {
            Id = "hotkey-only",
            Name = "Hotkey Only",
            HotkeyData = "Ctrl+Alt+E"
        });

        var result = _sut.MatchProfile("some-app", null, "hotkey-only");

        Assert.Equal(MatchKind.ManualOverride, result.Kind);
        Assert.Equal("hotkey-only", result.Profile!.Id);
    }

    [Fact]
    public void MatchProfile_EmptyMatcherProfileWithoutHotkey_RemainsGlobalFallback()
    {
        // Regression guard: the exclusion is gated on having a hotkey. A plain
        // no-matcher profile is still the global fallback as before.
        _sut.AddProfile(new Profile
        {
            Id = "global",
            Name = "Global"
        });

        var result = _sut.MatchProfile("some-app", null);

        Assert.Equal(MatchKind.Global, result.Kind);
        Assert.Equal("global", result.Profile!.Id);
    }
}