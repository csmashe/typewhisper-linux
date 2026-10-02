namespace TypeWhisper.Plugin.ParakeetCtc;

// Retain FluidAudio 0.15.5 adaptive-CBW settings with a stricter fork similarity default.
public static class CtcBiasPolicy
{
    public static float MinimumSimilarity(
        // ReSharper disable once UnusedParameter.Global -- keep the policy contract independent of vocabulary size.
        int termCount
    ) => .60f;

    public static double Bonus(int tokenCount) =>
        tokenCount <= 3 ? 4.5 : 4.5 * (1 + Math.Log2(tokenCount / 3d) * .3);

    public static bool Accept(double original, double preferred, int tokenCount) =>
        tokenCount > 0
        && double.IsFinite(original)
        && double.IsFinite(preferred)
        && preferred + Bonus(tokenCount) > original;
}
