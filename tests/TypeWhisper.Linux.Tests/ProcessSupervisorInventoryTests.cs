using System.Text.RegularExpressions;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed partial class ProcessSupervisorInventoryTests
{
    [Fact]
    public void Production_child_launches_exist_only_in_ProcessRunner()
    {
        var root = FindRepositoryRoot();
        var launchers = new List<string>();
        // ReSharper disable once LoopCanBeConvertedToQuery -- accumulating scan; the nested
        // skip-filters and the relative-path projection read better as loops
        foreach (var sourceRoot in new[] { "src", "plugins" })
        {
            var directory = Path.Join(root, sourceRoot);
            // ReSharper disable once LoopCanBeConvertedToQuery -- see above
            foreach (
                var file in Directory.EnumerateFiles(
                    directory,
                    "*.cs",
                    SearchOption.AllDirectories
                )
            )
            {
                if (
                    file.Contains(
                        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal
                    )
                    || file.Contains(
                        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                if (ChildLaunchRegex().IsMatch(File.ReadAllText(file)))
                {
                    launchers.Add(
                        Path.GetRelativePath(root, file)
                            .Replace(Path.DirectorySeparatorChar, '/')
                    );
                }
            }
        }

        Assert.Equal(
            ["src/TypeWhisper.Linux/Services/ProcessRunner.cs"],
            launchers.Order(StringComparer.Ordinal)
        );
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root from the test output directory."
        );
    }

    [GeneratedRegex(@"\bProcess\.Start\s*\(|new\s+Process\s*\(")]
    private static partial Regex ChildLaunchRegex();
}
