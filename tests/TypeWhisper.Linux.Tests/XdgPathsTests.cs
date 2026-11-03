using TypeWhisper.Core;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Env vars are mutated in place, safe only because the assembly disables test
///     parallelization.
/// </summary>
public sealed class XdgPathsTests : IDisposable
{
    private readonly string _tempHome = Path.Join(
        Path.GetTempPath(),
        "tw-xdg-paths-" + Guid.NewGuid().ToString("N")
    );

    private readonly string? _originalHome = Environment.GetEnvironmentVariable("HOME");
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public XdgPathsTests()
    {
        // BasePath freezes on first access from these same env vars; touch it before
        // redirecting them so this class cannot pin it to a temp dir.
        _ = TypeWhisperEnvironment.BasePath;
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("HOME", _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalDataHome);
        try
        {
            Directory.Delete(_tempHome, true);
        }
        catch
        {
            /* best effort */
        }
    }

    [Fact]
    public void AbsoluteValueIsHonoured()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/custom/data");

        Assert.Equal("/custom/data", XdgPaths.ResolveDataHome());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/data")]
    public void UnsetEmptyOrRelativeValueFallsBackToTheDefault(string? value)
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", value);

        Assert.Equal(Path.Join(_tempHome, ".local", "share"), XdgPaths.ResolveDataHome());
    }

    [Fact]
    public void FallbackStaysRootedWhenHomeDoesNotExistYet()
    {
        // Guards the empty string GetFolderPath's default option returns here.
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
        Environment.SetEnvironmentVariable("HOME", Path.Join(_tempHome, "not-created-yet"));

        var resolved = XdgPaths.ResolveDataHome();

        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(Path.Join(_tempHome, "not-created-yet", ".local", "share"), resolved);
    }
}
