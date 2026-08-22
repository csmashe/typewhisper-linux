using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AppHotkeyReconcileTests
{
    [Theory]
    [InlineData("first appearance", "first", "action-a", 1)]
    [InlineData("persistence", "persistent", "action-a,profile-b/", 2)]
    [InlineData("disappearance then reappearance", "reappears", "action-a//action-a", 1)]
    [InlineData("intra-pass duplicates", "duplicates", "action-a,profile-b", 2)]
    public void TransitionDynamicHotkeyRejections_LogsOnlyNewUniqueEntries(
        string description,
        string scenario,
        string expectedLogPasses,
        int expectedActiveCount
    )
    {
        _ = description; // Labels the theory case in test output only.
        var action = Rejection(
            DynamicHotkeyBindingKind.PromptAction,
            "action-a",
            "Action A",
            "Ctrl+Alt+A"
        );
        var profile = Rejection(
            DynamicHotkeyBindingKind.Profile,
            "profile-b",
            "Profile B",
            "Ctrl+Alt+B"
        );
        var passes = scenario switch
        {
            "first" => new[] { new[] { action } },
            "persistent" => new[] { new[] { action, profile }, new[] { action, profile } },
            "reappears" => new[] { new[] { action }, [], new[] { action } },
            "duplicates" => new[] { new[] { action, action, profile, action, profile } },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        IReadOnlySet<DynamicHotkeyRejection> active =
            new HashSet<DynamicHotkeyRejection>();
        var loggedPasses = new List<string>();

        foreach (var pass in passes)
        {
            var transition = App.TransitionDynamicHotkeyRejections(active, pass);
            loggedPasses.Add(
                string.Join(",", transition.NewlyActive.Select(item => item.BindingId))
            );
            active = transition.Active;
        }

        Assert.Equal(expectedLogPasses, string.Join("/", loggedPasses));
        Assert.Equal(expectedActiveCount, active.Count);
    }

    [Theory]
    [InlineData("no fixed setter succeeded (all-failed or mode-only)", false, false, false, false, false, false)]
    [InlineData("toggle", true, false, false, false, false, true)]
    [InlineData("prompt palette", false, true, false, false, false, true)]
    [InlineData("recent transcriptions", false, false, true, false, false, true)]
    [InlineData("copy last transcription", false, false, false, true, false, true)]
    [InlineData("transform selection", false, false, false, false, true, true)]
    public void ShouldReconcileDynamicHotkeys_ReflectsFixedSetterSuccessesOnly(
        string description,
        bool toggleChanged,
        bool promptPaletteChanged,
        bool recentTranscriptionsChanged,
        bool copyLastTranscriptionChanged,
        bool transformSelectionChanged,
        bool expected
    )
    {
        _ = description; // Labels the theory case in test output only.
        var actual = App.ShouldReconcileDynamicHotkeys(
            toggleChanged,
            promptPaletteChanged,
            recentTranscriptionsChanged,
            copyLastTranscriptionChanged,
            transformSelectionChanged
        );

        Assert.Equal(expected, actual);
    }

    private static DynamicHotkeyRejection Rejection(
        DynamicHotkeyBindingKind kind,
        string id,
        string displayName,
        string chord
    )
    {
        return new(
            kind,
            DynamicHotkeyRejectionReason.Conflict,
            id,
            displayName,
            chord
        );
    }
}
