using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

[Flags]
public enum UiFailureKind
{
    FileSystem = 1,
    StorageProvider = 2,
    Clipboard = 4,
    Window = 8,
}

/// <summary>
///     Contains expected UI-facing I/O and platform failures, records an English
///     diagnostic, rolls back caller-owned state, and presents a recoverable status.
///     Unexpected programming and fatal runtime failures are deliberately not caught.
/// </summary>
public sealed class UiOperationGuard
{
    private readonly Func<string, Task> _defaultPresenter;
    private readonly IErrorLogService _errorLog;
    private readonly Func<string, string, string> _failureMessageFormatter;

    public UiOperationGuard(
        IErrorLogService errorLog,
        Func<string, Task> defaultPresenter,
        Func<string, string, string> failureMessageFormatter
    )
    {
        ArgumentNullException.ThrowIfNull(errorLog);
        ArgumentNullException.ThrowIfNull(defaultPresenter);
        ArgumentNullException.ThrowIfNull(failureMessageFormatter);

        _errorLog = errorLog;
        _defaultPresenter = defaultPresenter;
        _failureMessageFormatter = failureMessageFormatter;
    }

    public bool Run(
        string operationName,
        string operationDisplayName,
        UiFailureKind expectedFailures,
        Action operation,
        Action? rollback = null,
        Func<string, Task>? presenter = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDisplayName);
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            operation();
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex, expectedFailures))
        {
            LogFailure(operationName, ex);
            SafeRollback(operationName, rollback);
            _ = SafePresentAsync(
                operationName,
                FormatFailure(operationDisplayName, ex),
                presenter ?? _defaultPresenter
            );
            return false;
        }
    }

    public async Task<bool> RunAsync(
        string operationName,
        string operationDisplayName,
        UiFailureKind expectedFailures,
        Func<Task> operation,
        Func<Task>? rollback = null,
        Func<string, Task>? presenter = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDisplayName);
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            await operation();
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex, expectedFailures))
        {
            LogFailure(operationName, ex);
            await SafeRollbackAsync(operationName, rollback);
            await SafePresentAsync(
                operationName,
                FormatFailure(operationDisplayName, ex),
                presenter ?? _defaultPresenter
            );
            return false;
        }
    }

    /// <summary>
    ///     Last-resort reporting for an exception already delivered by Avalonia's
    ///     dispatcher boundary. This method never rethrows non-fatal logger or
    ///     presenter failures.
    /// </summary>
    public Task ReportDispatcherFailureAsync(
        Exception exception,
        string operationDisplayName
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationDisplayName);

        const string operationName = "Avalonia UI dispatcher";
        LogFailure(operationName, exception);
        return SafePresentAsync(
            operationName,
            FormatFailure(operationDisplayName, exception),
            _defaultPresenter
        );
    }

    private static bool IsExpectedFailure(
        Exception exception,
        UiFailureKind expectedFailures
    )
    {
        return (
                expectedFailures.HasFlag(UiFailureKind.FileSystem)
                && exception is IOException or UnauthorizedAccessException
            )
            || (
                expectedFailures.HasFlag(UiFailureKind.StorageProvider)
                && exception is DBusExceptionBase or TimeoutException
            )
            || (
                expectedFailures.HasFlag(UiFailureKind.Clipboard)
                && exception
                    is TimeoutException
                        or ExternalException
                        or ObjectDisposedException
            )
            || (
                expectedFailures.HasFlag(UiFailureKind.Window)
                && exception
                    is DBusExceptionBase
                        or TimeoutException
                        or Win32Exception
                        or ExternalException
                        or ObjectDisposedException
            );
    }

    private string FormatFailure(string operationDisplayName, Exception exception)
    {
        try
        {
            return _failureMessageFormatter(operationDisplayName, exception.Message);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            SafeTrace(
                $"[UI] Failure message formatting for '{operationDisplayName}' failed: {ex}"
            );
            return $"{operationDisplayName} failed: {exception.Message}";
        }
    }

    private void LogFailure(string operationName, Exception exception)
    {
        var message =
            $"UI operation '{operationName}' failed with "
            + $"{exception.GetType().Name}: {exception.Message}";
        SafeTrace($"[UI] {message}{Environment.NewLine}{exception}");

        try
        {
            _errorLog.AddEntry(message);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            SafeTrace($"[UI] Error-log reporting failed: {ex}");
        }
    }

    private void SafeRollback(string operationName, Action? rollback)
    {
        if (rollback is null)
        {
            return;
        }

        try
        {
            rollback();
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            LogFailure($"{operationName} rollback", ex);
        }
    }

    private async Task SafeRollbackAsync(string operationName, Func<Task>? rollback)
    {
        if (rollback is null)
        {
            return;
        }

        try
        {
            await rollback();
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            LogFailure($"{operationName} rollback", ex);
        }
    }

    private async Task SafePresentAsync(
        string operationName,
        string message,
        Func<string, Task> presenter
    )
    {
        try
        {
            await presenter(message);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            LogFailure($"{operationName} failure presenter", ex);
        }
    }

    private static bool IsFatal(Exception exception)
    {
        return exception is OutOfMemoryException or AccessViolationException;
    }

    private static void SafeTrace(string message)
    {
        try
        {
            Trace.WriteLine(message);
        }
        catch
        {
            // Diagnostics must never become a second UI failure.
        }
    }
}
