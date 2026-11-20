using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Integration.Tests;

/// <summary>
///     Loads the source-tree catalogs into <see cref="Loc" /> before any test runs, so tests
///     that read localized text don't depend on another test having initialized it first.
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
            Path.Join(testDir, "..", "..", "src", "TypeWhisper.Linux", "Resources", "Localization")
        );
    }
}
