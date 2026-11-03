using TypeWhisper.Cli.Services;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class DiscoveryFileReaderTests : IDisposable
{
    private readonly string _configHome =
        Path.Join(Path.GetTempPath(), "typewhisper-cli-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly string? _originalConfigHome =
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public DiscoveryFileReaderTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalConfigHome);
        if (Directory.Exists(_configHome))
        {
            Directory.Delete(_configHome, recursive: true);
        }
    }

    [Fact]
    public void V2File_ParsesPortTokenAndSocketPath()
    {
        WriteDiscovery(
            """
            {"version":2,"port":9876,"token":"secret","socket_path":"/run/user/1000/typewhisper/api.sock"}
            """
        );

        var discovery = DiscoveryFileReader.TryRead();

        Assert.NotNull(discovery);
        Assert.Equal(2, discovery.Version);
        Assert.Equal(9876, discovery.Port);
        Assert.Equal("secret", discovery.Token);
        Assert.Equal("/run/user/1000/typewhisper/api.sock", discovery.SocketPath);
    }

    [Fact]
    public void V2FileWithoutSocketPath_ReturnsDiscoveryWithNullSocketPath()
    {
        WriteDiscovery("""{"version":2,"port":9876,"token":"secret"}""");

        var discovery = DiscoveryFileReader.TryRead();

        Assert.NotNull(discovery);
        Assert.Equal(2, discovery.Version);
        Assert.Null(discovery.SocketPath);
    }

    [Fact]
    public void V1FileWithoutSocketPath_RemainsLenient()
    {
        WriteDiscovery("""{"version":1,"port":9876,"token":"legacy"}""");

        var discovery = DiscoveryFileReader.TryRead();

        Assert.NotNull(discovery);
        Assert.Equal(1, discovery.Version);
        Assert.Equal(9876, discovery.Port);
        Assert.Equal("legacy", discovery.Token);
        Assert.Null(discovery.SocketPath);
    }

    [Fact]
    public void FileWithoutVersion_RemainsLenient()
    {
        WriteDiscovery(
            """{"port":9876,"token":"legacy","socket_path":"/tmp/typewhisper.sock"}"""
        );

        var discovery = DiscoveryFileReader.TryRead();

        Assert.NotNull(discovery);
        Assert.Null(discovery.Version);
        Assert.Equal(9876, discovery.Port);
        Assert.Equal("legacy", discovery.Token);
        Assert.Equal("/tmp/typewhisper.sock", discovery.SocketPath);
    }

    [Fact]
    public void FileWithNonNumericVersion_RemainsLenient()
    {
        WriteDiscovery(
            """{"version":"2","port":9876,"token":"secret","socket_path":"/tmp/typewhisper.sock"}"""
        );

        var discovery = DiscoveryFileReader.TryRead();

        Assert.NotNull(discovery);
        Assert.Null(discovery.Version);
        Assert.Equal(9876, discovery.Port);
        Assert.Equal("secret", discovery.Token);
        Assert.Equal("/tmp/typewhisper.sock", discovery.SocketPath);
    }

    [Fact]
    public void MalformedFile_ReturnsNull()
    {
        WriteDiscovery("""{"version":2,"port":""");

        Assert.Null(DiscoveryFileReader.TryRead());
    }

    private void WriteDiscovery(string json)
    {
        var directory = Path.Join(_configHome, "typewhisper");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "api-discovery.json"), json);
    }
}
