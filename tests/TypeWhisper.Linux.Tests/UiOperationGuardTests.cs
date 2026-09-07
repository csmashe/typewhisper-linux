using System.ComponentModel;
using System.Runtime.InteropServices;
using Moq;
using Tmds.DBus.Protocol;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class UiOperationGuardTests
{
    public static TheoryData<UiFailureKind, Exception> ExpectedFailures()
    {
        return new TheoryData<UiFailureKind, Exception>
        {
            { UiFailureKind.FileSystem, new IOException("disk full") },
            {
                UiFailureKind.FileSystem,
                new UnauthorizedAccessException("access denied")
            },
            {
                UiFailureKind.StorageProvider,
                new DBusErrorReplyException(
                    "org.freedesktop.portal.Error.Failed",
                    "portal failed"
                )
            },
            {
                UiFailureKind.StorageProvider,
                new TimeoutException("portal timed out")
            },
            {
                UiFailureKind.Window,
                new Win32Exception(5, "native platform failed")
            },
            {
                UiFailureKind.Clipboard,
                new ExternalException("clipboard platform failed")
            },
            {
                UiFailureKind.Clipboard,
                new ObjectDisposedException("clipboard")
            },
            {
                UiFailureKind.Clipboard,
                new TimeoutException("clipboard timed out")
            },
            {
                UiFailureKind.Window,
                new DBusErrorReplyException(
                    "org.freedesktop.portal.Error.Failed",
                    "window portal failed"
                )
            },
            {
                UiFailureKind.Window,
                new ObjectDisposedException("window")
            },
            {
                UiFailureKind.Window,
                new ExternalException("native window failed")
            },
        };
    }

    // Exception is not IXunitSerializable, so Test Explorer cannot pre-enumerate the
    // cases. They still all run, and the concrete instances are the point of the data.
#pragma warning disable xUnit1045
    [Theory]
    // Exception instances aren't serializable for row enumeration, and the real
    // exception types are the point of the theory.
    [MemberData(nameof(ExpectedFailures), DisableDiscoveryEnumeration = true)]
    public async Task RunAsync_ExpectedFailure_RollsBackThenPresentsAndLogs(
        UiFailureKind failureKind,
        Exception failure
    )
#pragma warning restore xUnit1045
    {
        var events = new List<string>();
        var errorLog = new Mock<IErrorLogService>();
        var guard = CreateGuard(
            errorLog,
            message =>
            {
                events.Add($"present:{message}");
                return Task.CompletedTask;
            }
        );

        var result = await guard.RunAsync(
            "export transcription",
            "Export",
            failureKind,
            () => Task.FromException(failure),
            () =>
            {
                events.Add("rollback");
                return Task.CompletedTask;
            }
        );

        Assert.False(result);
        Assert.Equal("rollback", events[0]);
        Assert.Equal($"present:Export failed: {failure.Message}", events[1]);
        errorLog.Verify(
            log => log.AddEntry(
                It.Is<string>(message =>
                    message.Contains(
                        "UI operation 'export transcription' failed",
                        StringComparison.Ordinal
                    )
                ),
                It.IsAny<string>()
            ),
            Times.Once
        );
    }

    [Fact]
    public void Run_SynchronousAction_ContainsExpectedFailure()
    {
        var events = new List<string>();
        var errorLog = new Mock<IErrorLogService>();
        var guard = CreateGuard(
            errorLog,
            message =>
            {
                events.Add($"present:{message}");
                return Task.CompletedTask;
            }
        );

        var result = guard.Run(
            "save profile",
            "Save",
            UiFailureKind.FileSystem,
            () => throw new IOException("read-only filesystem"),
            () => events.Add("rollback")
        );

        Assert.False(result);
        Assert.Equal(
            ["rollback", "present:Save failed: read-only filesystem"],
            events
        );
    }

    [Fact]
    public async Task RunAsync_PresenterThrows_DoesNotEscape()
    {
        var errorLog = new Mock<IErrorLogService>();
        var guard = CreateGuard(
            errorLog,
            _ => throw new InvalidOperationException("dialog failed")
        );

        var result = await guard.RunAsync(
            "select files",
            "Select files",
            UiFailureKind.FileSystem,
            () => Task.FromException(new IOException("portal failed"))
        );

        Assert.False(result);
        errorLog.Verify(
            log => log.AddEntry(
                It.Is<string>(message =>
                    message.Contains("failure presenter", StringComparison.Ordinal)
                ),
                It.IsAny<string>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task RunAsync_NonExpectedFailure_PropagatesWithoutRecovery()
    {
        var rollbackCalled = false;
        var presenterCalled = false;
        var errorLog = new Mock<IErrorLogService>();
        var guard = CreateGuard(
            errorLog,
            _ =>
            {
                presenterCalled = true;
                return Task.CompletedTask;
            }
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.RunAsync(
                "format profile",
                "Format",
                UiFailureKind.FileSystem,
                () =>
                    Task.FromException(
                        new InvalidOperationException("programming failure")
                    ),
                () =>
                {
                    rollbackCalled = true;
                    return Task.CompletedTask;
                }
            )
        );

        Assert.False(rollbackCalled);
        Assert.False(presenterCalled);
        errorLog.Verify(
            log => log.AddEntry(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task RunAsync_OutOfMemoryFailure_PropagatesWithoutRecovery()
    {
        var errorLog = new Mock<IErrorLogService>();
        var guard = CreateGuard(errorLog, _ => Task.CompletedTask);

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            guard.RunAsync(
                "allocate",
                "Allocate",
                UiFailureKind.FileSystem,
                () => Task.FromException(new OutOfMemoryException("fatal"))
            )
        );
    }

    private static UiOperationGuard CreateGuard(
        Mock<IErrorLogService> errorLog,
        Func<string, Task> presenter
    )
    {
        return new UiOperationGuard(
            errorLog.Object,
            presenter,
            (operation, reason) => $"{operation} failed: {reason}"
        );
    }
}
