using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

/// <summary>
///     Guards the <see cref="ErrorCategory" /> taxonomy: producers must pass a
///     canonical <c>ErrorCategory.*</c> constant to <c>AddEntry</c>, never a bare
///     string literal, so the About-screen filter and exported diagnostics stay in
///     sync with the constants.
/// </summary>
public partial class ErrorCategoryGuardTests
{
    // Matches an AddEntry(...) call that passes one of the known category words as a
    // *quoted literal* in an argument after the message (i.e. preceded by a comma).
    // [^;] stays inside a single statement and still spans newlines for wrapped calls.
    [GeneratedRegex(
        "AddEntry\\(\\s*[^;]*?,\\s*\"(general|transcription|recording|prompt|plugin|insertion|detection)\""
    )]
    private static partial Regex LiteralCategoryCallRegex();

    [Fact]
    public void Defaults_reference_the_General_constant_not_a_magic_string()
    {
        // ErrorLogEntry.Create's default category must be the constant, not "general".
        Assert.Equal(ErrorCategory.General, ErrorLogEntry.Create("msg").Category);
    }

    [Fact]
    public void Category_constants_are_distinct_and_lowercase()
    {
        string[] all =
        [
            ErrorCategory.General,
            ErrorCategory.Transcription,
            ErrorCategory.Recording,
            ErrorCategory.Prompt,
            ErrorCategory.Plugin,
            ErrorCategory.Insertion,
            ErrorCategory.Detection,
        ];

        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.All(all, c => Assert.Equal(c.ToLowerInvariant(), c));
    }

    [Fact]
    public void No_production_AddEntry_call_passes_a_literal_category()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var dir in new[] { "src", "plugins" })
        {
            var subtree = Path.Join(root, dir);
            if (!Directory.Exists(subtree))
            {
                continue;
            }

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var file in Directory.EnumerateFiles(subtree, "*.cs", SearchOption.AllDirectories))
            {
                // Skip build output.
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (LiteralCategoryCallRegex().IsMatch(text))
                {
                    offenders.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AddEntry must be called with an ErrorCategory.* constant, not a string literal. "
            + "Offending files:\n  " + string.Join("\n  ", offenders)
        );
    }

    // Walk up from the test assembly until we find the repo root (holds both src/ and plugins/).
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Join(dir.FullName, "src"))
                && Directory.Exists(Path.Join(dir.FullName, "plugins")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root (a directory containing both 'src' and 'plugins') "
            + $"from {AppContext.BaseDirectory}."
        );
    }
}
