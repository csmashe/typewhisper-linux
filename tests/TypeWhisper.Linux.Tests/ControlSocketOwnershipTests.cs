using System.Net.Sockets;
using System.Runtime.InteropServices;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ControlSocketOwnershipTests
{
    private const UnixFileMode LockFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan s_guard = TimeSpan.FromSeconds(2);

    [Fact]
    public void OwnershipLock_IsExclusivePersistentAndReusable()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-m7");
        var socketPath = Path.Join(tempDirectory, "control.sock");
        var lockPath = Path.Join(tempDirectory, "control.lock");
        ControlSocketOwnership? ownerA = null;
        ControlSocketOwnership? ownerC = null;

        try
        {
            Assert.True(ControlSocketOwnership.TryAcquire(socketPath, out ownerA));
            Assert.Equal(lockPath, ownerA.LockPath);
            Assert.True(File.Exists(lockPath));

            Assert.False(ControlSocketOwnership.TryAcquire(socketPath, out var contender));
            Assert.Null(contender);
            Assert.True(File.Exists(lockPath));

            ownerA.Dispose();
            ownerA.Dispose();
            ownerA = null;

            Assert.True(File.Exists(lockPath));
            Assert.True(ControlSocketOwnership.TryAcquire(socketPath, out ownerC));
            Assert.True(File.Exists(lockPath));
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            Assert.Equal(LockFileMode, File.GetUnixFileMode(lockPath));
#pragma warning restore CA1416
        }
        finally
        {
            ownerA?.Dispose();
            ownerC?.Dispose();
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task RefusedClientCleanup_CannotUnlinkBoundOwnerBeforeListen()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-m7");
        var socketPath = Path.Join(tempDirectory, "control.sock");
        var bindComplete = NewGate();
        var allowListen = NewGate();
        var listenComplete = NewGate();
        var connectionAccepted = NewGate();

        var ownerTask = Task.Run(async () =>
        {
            ControlSocketOwnership? ownership = null;
            Socket? listener = null;
            try
            {
                Assert.True(
                    ControlSocketOwnership.TryAcquire(socketPath, out ownership)
                );
                listener = CreateSocket();
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                bindComplete.TrySetResult();

                await allowListen.Task.WaitAsync(s_guard);
                listener.Listen(8);
                listenComplete.TrySetResult();

                using var accepted = await listener.AcceptAsync().WaitAsync(s_guard);
                connectionAccepted.TrySetResult();
            }
            catch (Exception ex)
            {
                bindComplete.TrySetException(ex);
                listenComplete.TrySetException(ex);
                connectionAccepted.TrySetException(ex);
                throw;
            }
            finally
            {
                listener?.Dispose();
                ownership?.Dispose();
            }
        });

        try
        {
            await bindComplete.Task.WaitAsync(s_guard);
            Assert.True(File.Exists(socketPath));

            Assert.False(ControlSocketClient.IsLivePeer(socketPath));
            Assert.True(File.Exists(socketPath));

            Assert.False(ControlSocketClient.TrySendToggle(socketPath, out var toggleError));
            Assert.Null(toggleError);
            Assert.True(File.Exists(socketPath));

            var request = new JsonControlProtocol.Request
            {
                Version = JsonControlProtocol.CurrentVersion,
                Command = JsonControlProtocol.CmdStatus
            };
            Assert.False(
                ControlSocketClient.TrySendJson(
                    socketPath,
                    request,
                    out var responseJson,
                    out var jsonError
                )
            );
            Assert.Empty(responseJson);
            Assert.Null(jsonError);
            Assert.True(File.Exists(socketPath));

            allowListen.TrySetResult();
            await listenComplete.Task.WaitAsync(s_guard);

            using var client = CreateSocket();
            await client
                .ConnectAsync(new UnixDomainSocketEndPoint(socketPath))
                .WaitAsync(s_guard);
            await connectionAccepted.Task.WaitAsync(s_guard);
        }
        finally
        {
            allowListen.TrySetResult();
            try
            {
                await ownerTask.WaitAsync(s_guard);
            }
            finally
            {
                TestPaths.DeleteDirectory(tempDirectory);
            }
        }
    }

    [Fact]
    public async Task StaleSocket_IsCleanedAndReboundWhileOwnershipIsHeld()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-m7");
        var socketPath = Path.Join(tempDirectory, "control.sock");
        var boundPath = Path.Join(tempDirectory, "stale-source.sock");
        ControlSocketOwnership? ownership = null;
        Socket? listener = null;
        Socket? client = null;

        try
        {
            using (var stale = CreateSocket())
            {
                stale.Bind(new UnixDomainSocketEndPoint(boundPath));

                // SafeSocketHandle unlinks its original bound pathname on orderly disposal.
                // A second hard link preserves the same socket inode to model the pathname
                // left behind by a process that exits without managed cleanup.
                var result = link(boundPath, socketPath);
                Assert.True(
                    result == 0,
                    $"Could not preserve stale socket inode (errno {Marshal.GetLastPInvokeError()})."
                );
            }

            Assert.True(File.Exists(socketPath));
            Assert.True(ControlSocketOwnership.TryAcquire(socketPath, out ownership));
            Assert.Equal(
                ControlSocketCleanupResult.Removed,
                ownership.CleanupStaleSocket()
            );
            Assert.False(File.Exists(socketPath));

            listener = CreateSocket();
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);

            var acceptTask = listener.AcceptAsync();
            client = CreateSocket();
            await client
                .ConnectAsync(new UnixDomainSocketEndPoint(socketPath))
                .WaitAsync(s_guard);
            using var accepted = await acceptTask.WaitAsync(s_guard);
            Assert.True(client.Connected);
        }
        finally
        {
            client?.Dispose();
            listener?.Dispose();
            ownership?.Dispose();
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task FreshProbe_LeavesLiveListenerPathIntact()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-m7");
        var socketPath = Path.Join(tempDirectory, "control.sock");
        Socket? listener = null;

        try
        {
            listener = CreateSocket();
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);

            var probeAcceptTask = listener.AcceptAsync();
            Assert.Equal(
                ControlSocketCleanupResult.Live,
                ControlSocketOwnership.TryCleanupStaleSocket(socketPath)
            );
            using (await probeAcceptTask.WaitAsync(s_guard))
            {
                Assert.True(File.Exists(socketPath));
            }

            var secondAcceptTask = listener.AcceptAsync();
            using var client = CreateSocket();
            await client
                .ConnectAsync(new UnixDomainSocketEndPoint(socketPath))
                .WaitAsync(s_guard);
            using var secondConnection = await secondAcceptTask.WaitAsync(s_guard);
            Assert.True(File.Exists(socketPath));
        }
        finally
        {
            listener?.Dispose();
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void IndeterminateProbe_LeavesSocketPathIntact()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ipc-m7");
        var socketPath = Path.Join(tempDirectory, "control.sock");
        ControlSocketOwnership? ownership = null;
        Socket? listener = null;

        try
        {
            listener = CreateSocket();
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            File.SetUnixFileMode(socketPath, UnixFileMode.None);
#pragma warning restore CA1416

            Assert.True(ControlSocketOwnership.TryAcquire(socketPath, out ownership));
            Assert.Equal(
                ControlSocketCleanupResult.Indeterminate,
                ownership.CleanupStaleSocket()
            );
            Assert.True(File.Exists(socketPath));
        }
        finally
        {
            if (File.Exists(socketPath))
            {
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    private static TaskCompletionSource NewGate()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Test-only: simulates the pathname a crashed process leaves behind. DllImport
    // (not LibraryImport) avoids requiring unsafe code here, so SYSLIB1054 is declined.
    // CA2101 wants Unicode marshaling, wrong for libc — POSIX pathnames are byte
    // strings, so LPUTF8Str is the correct encoding.
#pragma warning disable SYSLIB1054, CA2101
    // ReSharper disable once InconsistentNaming -- mirrors the native libc function.
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath
    );
#pragma warning restore SYSLIB1054, CA2101
}
