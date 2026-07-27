using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ApiDiscoveryFileTests : IDisposable
{
    private readonly string _xdgConfigHome;
    private readonly string? _originalXdgConfigHome;

    public ApiDiscoveryFileTests()
    {
        _originalXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        _xdgConfigHome = Path.Join(Path.GetTempPath(), "typewhisper-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdgConfigHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalXdgConfigHome);
        if (Directory.Exists(_xdgConfigHome))
        {
            Directory.Delete(_xdgConfigHome, recursive: true);
        }
    }

    [Fact]
    public void Write_CreatesJsonWithVersionPortTokenAndSocketPath()
    {
        var sut = new ApiDiscoveryFile();

        Assert.True(sut.Write(9876, "supersecret", "/tmp/typewhisper/api.sock"));

        var path = Path.Join(_xdgConfigHome, "typewhisper", "api-discovery.json");
        Assert.True(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(9876, root.GetProperty("port").GetInt32());
        Assert.Equal("supersecret", root.GetProperty("token").GetString());
        Assert.Equal(
            "/tmp/typewhisper/api.sock",
            root.GetProperty("socket_path").GetString()
        );
    }

    [Fact]
    public void Write_ReportsFailureWhenDirectoryCannotBeCreated()
    {
        Directory.CreateDirectory(_xdgConfigHome);
        File.WriteAllText(Path.Join(_xdgConfigHome, "typewhisper"), "not a directory");

        Assert.False(new ApiDiscoveryFile().Write(9876, "tok", "/tmp/api.sock"));
    }

    [Fact]
    public void Write_AtomicTempThenRename()
    {
        var sut = new ApiDiscoveryFile();
        sut.Write(9876, "tok", "/tmp/api.sock");

        var dir = Path.Join(_xdgConfigHome, "typewhisper");
        var leftoverTmp = Directory.GetFiles(dir, "*.tmp");
        Assert.Empty(leftoverTmp);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var sut = new ApiDiscoveryFile();
        sut.Write(9876, "tok", "/tmp/api.sock");
        var path = Path.Join(_xdgConfigHome, "typewhisper", "api-discovery.json");
        Assert.True(File.Exists(path));

        sut.Delete();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_MissingFile_DoesNotThrow()
    {
        var sut = new ApiDiscoveryFile();
        sut.Delete();
        sut.Delete();
    }

    [Fact]
    public void Write_SetsUnix0600Mode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var sut = new ApiDiscoveryFile();
        sut.Write(9876, "tok", "/tmp/api.sock");

        var path = Path.Join(_xdgConfigHome, "typewhisper", "api-discovery.json");
        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Write_SetsDirectoryMode0700()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var sut = new ApiDiscoveryFile();
        sut.Write(9876, "tok", "/tmp/api.sock");

        var dir = Path.Join(_xdgConfigHome, "typewhisper");
        var mode = File.GetUnixFileMode(dir);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            mode
        );
    }

    [Fact]
    public void Write_OverwritesPreexistingFile()
    {
        var sut = new ApiDiscoveryFile();
        sut.Write(1234, "old", "/tmp/old-api.sock");
        sut.Write(9876, "new", "/tmp/new-api.sock");

        var path = Path.Join(_xdgConfigHome, "typewhisper", "api-discovery.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(9876, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("new", doc.RootElement.GetProperty("token").GetString());
        Assert.Equal(
            "/tmp/new-api.sock",
            doc.RootElement.GetProperty("socket_path").GetString()
        );
    }

    [Fact]
    public void Write_StaleTempIsReplaced()
    {
        // Simulate a crash that left an orphaned tmp from a previous run.
        // Without the stale-delete, FileMode.CreateNew would throw IOException
        // here and the new write would be lost.
        var dir = Path.Join(_xdgConfigHome, "typewhisper");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "api-discovery.json.tmp"), "leftover");

        var sut = new ApiDiscoveryFile();
        sut.Write(9876, "tok", "/tmp/api.sock");

        var path = Path.Join(dir, "api-discovery.json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Join(dir, "api-discovery.json.tmp")));
    }
}
