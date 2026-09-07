using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>
///     Source-level guard: production settings writes must go through the transactional
///     ISettingsService.Update, never the Save(Current with ...) read-modify-write idiom.
///     Scope limit (deliberate): matches the conventional receivers `settings`/`_settings`,
///     which covers every ISettingsService field and parameter in the tree today; a
///     differently named receiver would need this regex extended.
/// </summary>
public partial class SettingsUpdateGuardTests
{
    [Fact]
    public void Production_settings_writes_use_transactional_update()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Join(root, "src");
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            scanned++;
            var source = File.ReadAllText(file);
            foreach (Match match in SettingsSaveRegex().Matches(source))
            {
                var line = source[..match.Index].Count(character => character == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}");
            }
        }

        Assert.True(scanned > 0, $"No production source files found under {sourceRoot}.");
        Assert.True(
            offenders.Count == 0,
            "Production settings read-modify-write calls must use ISettingsService.Update. "
            + "Offenders:\n  " + string.Join("\n  ", offenders)
        );
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // TypeWhisper.slnx is unique to this repository, so an unrelated ancestor
            // that merely contains src/ and tests/ directories cannot match.
            if (File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root from {AppContext.BaseDirectory}."
        );
    }

    [GeneratedRegex(@"\b(?:_settings|settings)\s*\.\s*Save\s*\(")]
    private static partial Regex SettingsSaveRegex();
}
