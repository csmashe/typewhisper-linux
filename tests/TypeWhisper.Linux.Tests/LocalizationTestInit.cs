using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Initializes <see cref="Loc" /> once for the whole test assembly, pointed
///     at the source-tree English catalog, so ViewModels that route status text
///     through Loc resolve to real English instead of the bare key. Runs before
///     any test via [ModuleInitializer]. Language stays the default "en".
/// </summary>
internal static class LocalizationTestInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        Loc.Instance.Initialize(LocalizationDir());
    }

    private static string LocalizationDir([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Combine(testDir, "..", "..", "src", "TypeWhisper.Linux", "Resources", "Localization")
        );
    }
}
