using System.Text.RegularExpressions;
using System.Xml.Linq;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Guards the Light/Dark palette in Resources/ThemeColors.axaml: both variants define the same
///     keys, and every Tw* key a view references exists, since a missing DynamicResource renders
///     nothing instead of failing the build.
/// </summary>
public sealed partial class ThemeColorsTests
{
    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void LightAndDark_DefineTheSameKeys()
    {
        var variants = LoadVariants();

        Assert.NotEmpty(variants["Dark"]);
        Assert.Equal(variants["Dark"].Order(StringComparer.Ordinal), variants["Light"].Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryReferencedThemeKey_IsDefined()
    {
        var defined = LoadVariants()["Dark"];
        var root = Path.Join(FindRepositoryRoot(), "src", "TypeWhisper.Linux");

        var missing = Directory
            .EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path => ThemeKeyReference().Matches(File.ReadAllText(path))
                .Select(match => $"{Path.GetFileName(path)}: {match.Groups[1].Value}"))
            .Where(reference => !defined.Contains(reference[(reference.IndexOf(": ", StringComparison.Ordinal) + 2)..]))
            .ToList();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(InterfaceTheme.System, "Default")]
    [InlineData(InterfaceTheme.Light, "Light")]
    [InlineData(InterfaceTheme.Dark, "Dark")]
    [InlineData((InterfaceTheme)42, "Dark")]
    public void ToThemeVariant_MapsSavedTheme(InterfaceTheme theme, string expected)
    {
        Assert.Equal(expected, InterfaceThemeService.ToThemeVariant(theme).Key.ToString());
    }

    private static Dictionary<string, HashSet<string>> LoadVariants()
    {
        var path = Path.Join(FindRepositoryRoot(), "src", "TypeWhisper.Linux", "Resources", "ThemeColors.axaml");
        return XDocument.Load(path)
            .Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary" && element.Attribute(s_xaml + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(s_xaml + "Key")!.Value,
                element => element.Elements()
                    .Select(child => child.Attribute(s_xaml + "Key")?.Value)
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal));
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

        throw new DirectoryNotFoundException("Could not find the repository root from the test output directory.");
    }

    [GeneratedRegex(@"DynamicResource\s+(Tw\w+)")]
    private static partial Regex ThemeKeyReference();
}
