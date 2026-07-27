using System.Net.Sockets;
using System.Runtime.InteropServices;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ApiSocketOwnershipTests
{
    private const UnixFileMode LockFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan s_guard = TimeSpan.FromSeconds(2);

    [Fact]
    public void ApiAndControlOwnership_AreExclusivePersistentAndIndependent()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-api");
        var apiSocketPath = Path.Join(tempDirectory, "api.sock");
        var controlSocketPath = Path.Join(tempDirectory, "control.sock");
        var apiLockPath = Path.Join(tempDirectory, "api.lock");
        ApiSocketOwnership? apiOwner = null;
        ApiSocketOwnership? reusedApiOwner = null;
        ControlSocketOwnership? controlOwner = null;

        try
        {
            Assert.True(ApiSocketOwnership.TryAcquire(apiSocketPath, out apiOwner));
            Assert.True(
                ControlSocketOwnership.TryAcquire(controlSocketPath, out controlOwner)
            );
            Assert.Equal(apiLockPath, apiOwner.LockPath);
            Assert.NotEqual(apiOwner.LockPath, controlOwner.LockPath);

            Assert.False(
                ApiSocketOwnership.TryAcquire(apiSocketPath, out var contender)
            );
            Assert.Null(contender);

            apiOwner.Dispose();
            apiOwner.Dispose();
            apiOwner = null;

            Assert.True(File.Exists(apiLockPath));
            Assert.True(
                ApiSocketOwnership.TryAcquire(apiSocketPath, out reusedApiOwner)
            );
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            Assert.Equal(LockFileMode, File.GetUnixFileMode(apiLockPath));
#pragma warning restore CA1416
        }
        finally
        {
            apiOwner?.Dispose();
            reusedApiOwner?.Dispose();
            controlOwner?.Dispose();
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task StaleSocket_IsRemovedOnlyAfterConnectionRefused()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-api");
        var socketPath = Path.Join(tempDirectory, "api.sock");
        var boundPath = Path.Join(tempDirectory, "stale-source.sock");
        ApiSocketOwnership? ownership = null;

        try
        {
            using (var stale = CreateSocket())
            {
                stale.Bind(new UnixDomainSocketEndPoint(boundPath));
                var result = link(boundPath, socketPath);
                Assert.True(
                    result == 0,
                    $"Could not preserve stale socket inode (errno {Marshal.GetLastPInvokeError()})."
                );
            }

            Assert.True(ApiSocketOwnership.TryAcquire(socketPath, out ownership));
            Assert.Equal(
                ApiSocketCleanupResult.Removed,
                ownership.CleanupStaleSocket()
            );
            Assert.False(File.Exists(socketPath));

            using var listener = CreateSocket();
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);
            var acceptTask = listener.AcceptAsync();
            using var client = CreateSocket();
            await client
                .ConnectAsync(new UnixDomainSocketEndPoint(socketPath))
                .WaitAsync(s_guard);
            using var accepted = await acceptTask.WaitAsync(s_guard);
            Assert.True(client.Connected);
        }
        finally
        {
            ownership?.Dispose();
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task LiveSocket_IsPreserved()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-api");
        var socketPath = Path.Join(tempDirectory, "api.sock");

        try
        {
            using var listener = CreateSocket();
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);

            var acceptTask = listener.AcceptAsync();
            Assert.Equal(
                ApiSocketCleanupResult.Live,
                ApiSocketOwnership.TryCleanupStaleSocket(socketPath)
            );
            using var accepted = await acceptTask.WaitAsync(s_guard);
            Assert.True(File.Exists(socketPath));
        }
        finally
        {
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void IndeterminateProbe_FailsClosedAndPreservesPath()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-api");
        var socketPath = Path.Join(tempDirectory, "api.sock");
        ApiSocketOwnership? ownership = null;
        Socket? listener = null;

        try
        {
            listener = CreateSocket();
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            File.SetUnixFileMode(socketPath, UnixFileMode.None);
#pragma warning restore CA1416

            Assert.True(ApiSocketOwnership.TryAcquire(socketPath, out ownership));
            Assert.Equal(
                ApiSocketCleanupResult.Indeterminate,
                ownership.CleanupStaleSocket()
            );
            Assert.True(File.Exists(socketPath));
        }
        finally
        {
            if (File.Exists(socketPath))
            {
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
                File.SetUnixFileMode(
                    socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                );
#pragma warning restore CA1416
            }

            listener?.Dispose();
            ownership?.Dispose();
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    private static Socket CreateSocket()
    {
        return new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    }

#pragma warning disable SYSLIB1054, CA2101
    // ReSharper disable once InconsistentNaming -- mirrors the native libc function.
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath
    );
#pragma warning restore SYSLIB1054, CA2101
}
