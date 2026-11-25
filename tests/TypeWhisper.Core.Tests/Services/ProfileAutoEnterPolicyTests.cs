using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class ProfileAutoEnterPolicyTests
{
    [Theory]
    [InlineData(ProfileAutoEnterMode.SpokenCommand, false, "hello", false, false)]
    [InlineData(ProfileAutoEnterMode.SpokenCommand, true, "hello", false, true)]
    [InlineData(ProfileAutoEnterMode.SpokenCommand, true, "", false, true)]
    [InlineData(ProfileAutoEnterMode.Never, false, "hello", false, false)]
    [InlineData(ProfileAutoEnterMode.Always, false, "hello", false, true)]
    [InlineData(ProfileAutoEnterMode.Always, false, "  ", false, false)]
    [InlineData(ProfileAutoEnterMode.Always, false, "hello", true, false)]
    [InlineData(ProfileAutoEnterMode.Always, true, "hello", true, true)]
    [InlineData(ProfileAutoEnterMode.Always, true, "", false, true)]
    public void ShouldPressEnter_FollowsTheModeAndAnExplicitSpokenCommand(
        ProfileAutoEnterMode mode, bool spoken, string text, bool processingFailed, bool expected)
    {
        Assert.Equal(expected, ProfileAutoEnterPolicy.ShouldPressEnter(mode, spoken, text, processingFailed));
    }
}
