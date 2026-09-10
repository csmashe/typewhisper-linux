using System.Text.Json;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class CliConnectionResolverTests : IDisposable
{
    // The resolver appends "TypeWhisper" itself, so point it at an empty parent.
    private readonly string _root =
        Path.Join(Path.GetTempPath(), "typewhisper-cli-resolve-" + Guid.NewGuid().ToString("N"));

    public CliConnectionResolverTests()
    {
        Directory.CreateDirectory(Path.Join(_root, "TypeWhisper"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    /// <summary>
    ///     The no-override, no-discovery fallback has to match the port the app's API server
    ///     binds by default (AppSettings.ApiServerPort). The CLI cannot reference that constant,
    ///     so this test is what keeps the two from drifting apart.
    /// </summary>
    [Fact]
    public void Resolve_WithNoOverrideAndNoDiscoveryFile_UsesTheApiServerDefaultPort()
    {
        var connection = CliConnectionResolver.Resolve(
            new CliConnectionOptions(ApplicationDataRoot: _root)
        );

        Assert.Equal(9876, connection.Port);
        Assert.Null(connection.ApiToken);
    }

    [Fact]
    public void Resolve_WithDiscoveryFile_PrefersItsPortAndToken()
    {
        WriteDiscovery(new { version = 2, port = 5555, token = "from-discovery" });

        var connection = CliConnectionResolver.Resolve(
            new CliConnectionOptions(ApplicationDataRoot: _root)
        );

        Assert.Equal(5555, connection.Port);
        Assert.Equal("from-discovery", connection.ApiToken);
    }

    [Fact]
    public void Resolve_WithOutOfRangeDiscoveryPort_FallsBackToTheDefault()
    {
        WriteDiscovery(new { version = 2, port = 70000, token = "from-discovery" });

        var connection = CliConnectionResolver.Resolve(
            new CliConnectionOptions(ApplicationDataRoot: _root)
        );

        Assert.Equal(9876, connection.Port);
        Assert.Equal("from-discovery", connection.ApiToken);
    }

    [Fact]
    public void Resolve_WithOverrides_PrefersThemOverDiscovery()
    {
        WriteDiscovery(new { version = 2, port = 5555, token = "from-discovery" });

        var connection = CliConnectionResolver.Resolve(
            new CliConnectionOptions(
                ApplicationDataRoot: _root,
                PortOverride: 4242,
                ApiTokenOverride: "from-flag"
            )
        );

        Assert.Equal(4242, connection.Port);
        Assert.Equal("from-flag", connection.ApiToken);
    }

    private void WriteDiscovery(object payload) =>
        File.WriteAllText(
            Path.Join(_root, "TypeWhisper", "api-discovery.json"),
            JsonSerializer.Serialize(payload)
        );
}
