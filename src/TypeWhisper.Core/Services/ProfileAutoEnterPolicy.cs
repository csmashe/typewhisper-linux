using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>Decides whether Enter follows a dictation's insertion under a profile's <see cref="ProfileAutoEnterMode" />.</summary>
public static class ProfileAutoEnterPolicy
{
    /// <summary>
    ///     A spoken "press enter" always submits, even on its own. Always adds Enter after any non-empty text,
    ///     but not after post-processing failed or cleanup fell back to Light, so such text isn't sent unseen.
    /// </summary>
    public static bool ShouldPressEnter(
        ProfileAutoEnterMode mode,
        bool spokenPressEnter,
        string insertedText,
        bool processingFailed
    )
    {
        if (spokenPressEnter)
        {
            return true;
        }

        return mode == ProfileAutoEnterMode.Always
               && !processingFailed
               && !string.IsNullOrWhiteSpace(insertedText);
    }
}
