using System.Diagnostics;
using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Tracks the current login session through <c>org.freedesktop.login1</c> on the system
///     bus. A missing system bus or logind service is a supported configuration: in that case
///     the monitor stays permanently allowed so non-systemd distributions retain the previous
///     evdev behavior.
/// </summary>
internal sealed partial class LogindSessionActivityMonitor : ISessionActivityMonitor
{
    private const string LoginService = "org.freedesktop.login1";
    private const string ManagerPath = "/org/freedesktop/login1";
    private const string ManagerInterface = "org.freedesktop.login1.Manager";
    private const string SessionInterface = "org.freedesktop.login1.Session";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(4);

    private static readonly MessageValueReader<string> s_readObjectPath =
        static (message, _) => message.GetBodyReader().ReadObjectPath().ToString();

    private static readonly MessageValueReader<Dictionary<string, VariantValue>>
        s_readProperties = static (message, _) =>
            message.GetBodyReader().ReadDictionaryOfStringToVariantValue();

    private static readonly MessageValueReader<SessionPropertiesChanged> s_readPropertiesChanged =
        static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var interfaceName = reader.ReadString();
            var changed = reader.ReadDictionaryOfStringToVariantValue();
            var invalidated = new List<string>();
            var end = reader.ReadArrayStart(DBusType.String);
            while (reader.HasNext(end))
            {
                invalidated.Add(reader.ReadString());
            }

            return new SessionPropertiesChanged(interfaceName, changed, invalidated);
        };

    private static readonly MessageValueReader<bool> s_readLockSignal = static (_, _) => true;
    private static readonly MessageValueReader<bool> s_readUnlockSignal = static (_, _) => false;

    // The User.Display property Get returns a variant wrapping a (session-id, object-path) struct;
    // item 1 is the graphical session's object path.
    private static readonly MessageValueReader<string> s_readDisplaySessionPath = static (message, _) =>
    {
        var display = message.GetBodyReader().ReadVariantValue();
        return display.Count >= 2 ? display.GetItem(1).GetObjectPathAsString() : string.Empty;
    };

    private readonly Lock _lock = new();
    private readonly List<IDisposable> _subscriptions = [];

    private bool _active = true;
    private DBusConnection? _connection;
    private bool _disposed;
    private bool _established;
    private bool _fallbackAllowed;
    private int _failureScheduled;
    private Task? _initialization;
    private bool _isInputAllowed = true;
    private bool _lockedHint;
    private int _loggedUnavailable;
    private string? _sessionPath;
    private long _stateVersion;

    public bool IsInputAllowed
    {
        get
        {
            lock (_lock)
            {
                return _isInputAllowed;
            }
        }
    }

    public event EventHandler? InputAllowedChanged;

    public Task InitializeAsync(CancellationToken ct)
    {
        Task initialization;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initialization ??= InitializeCoreAsync();
        }

        return ct.CanBeCanceled ? initialization.WaitAsync(ct) : initialization;
    }

    public ValueTask DisposeAsync()
    {
        Transport transport;
        lock (_lock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _stateVersion++;
            transport = DetachTransport_NoLock();
        }

        DisposeTransport(transport);
        return ValueTask.CompletedTask;
    }

    internal static bool DeriveInputAllowed(bool active, bool lockedHint)
    {
        return active && !lockedHint;
    }

    private async Task InitializeCoreAsync()
    {
        DBusConnection? ownedConnection = null;
        var ownedSubscriptions = new List<IDisposable>();
        try
        {
            var address = DBusAddress.System
                ?? throw new LogindAbsentException("No system bus address available.");
            var connection = new DBusConnection(address);
            ownedConnection = connection;
            await connection.ConnectAsync().AsTask().WaitAsync(s_callTimeout).ConfigureAwait(false);

            var sessionPath = await ResolveSessionPathAsync(connection).ConfigureAwait(false);

            // logind is present and our session is resolved: this is a supported systemd host.
            // From here any monitoring fault must fail closed (block input) rather than fall back
            // to legacy fail-open, which is reserved for an absent bus/service.
            lock (_lock)
            {
                _established = true;
            }

            ownedSubscriptions.Add(
                await WatchPropertiesAsync(connection, sessionPath).ConfigureAwait(false)
            );
            ownedSubscriptions.Add(
                await WatchLockSignalAsync(connection, sessionPath, "Lock", true)
                    .ConfigureAwait(false)
            );
            ownedSubscriptions.Add(
                await WatchLockSignalAsync(connection, sessionPath, "Unlock", false)
                    .ConfigureAwait(false)
            );

            lock (_lock)
            {
                // ObjectDisposedException.ThrowIf takes a single 'disposed' flag; folding the
                // separate _fallbackAllowed state into it would misrepresent the guard.
#pragma warning disable CA1513
                if (_disposed || _fallbackAllowed)
                {
                    throw new ObjectDisposedException(nameof(LogindSessionActivityMonitor));
                }
#pragma warning restore CA1513

                _connection = connection;
                _sessionPath = sessionPath;
                _subscriptions.AddRange(ownedSubscriptions);
                ownedConnection = null;
                ownedSubscriptions.Clear();
            }

            // The subscriptions are installed before GetAll. A state-version check in Refresh
            // prevents a signal delivered between the reply and its continuation from being lost.
            await RefreshPropertiesAsync(connection, sessionPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DisposeTransport(new Transport(ownedConnection, ownedSubscriptions));
            EnterUnavailableFallback(ex);
        }
    }

    // Legacy fail-open is only for a genuinely absent logind (non-systemd / no system bus). A
    // present-but-erroring host — timeout, access denied, malformed reply — must fail closed so a
    // transient startup fault cannot restore raw keyboard access through later locks.
    private static bool IndicatesLogindAbsent(Exception ex)
    {
        return ex switch
        {
            LogindAbsentException => true, // no system bus address resolved at all
            DBusConnectFailedException => true, // bus socket missing / connection refused
            DBusErrorReplyException dbus =>
                dbus.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
                    or "org.freedesktop.DBus.Error.NameHasNoOwner"
                    or "org.freedesktop.DBus.Error.FileNotFound",
            _ => false
        };
    }

    // The one genuinely-unsupported case (no system bus) — the only path that stays fail-open.
    private sealed class LogindAbsentException(string message) : Exception(message);

    private static async Task<string> ResolveSessionPathAsync(DBusConnection connection)
    {
        // Prefer the caller's own session. This fails for processes outside a logind session
        // scope (e.g. GNOME/XDG-autostart apps under user@.service/app.slice), so fall back to
        // the user's graphical Display session rather than failing open with no lock gating.
        var sessionId = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
        try
        {
            var path = string.IsNullOrWhiteSpace(sessionId)
                ? await CallManagerForPathAsync(connection, "GetSessionByPID", 0).ConfigureAwait(false)
                : await CallGetSessionAsync(connection, sessionId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[SessionActivityMonitor] Caller session unresolved; using graphical session: {ex.Message}"
            );
        }

        return await ResolveDisplaySessionPathAsync(connection).ConfigureAwait(false);
    }

    private static async Task<string> CallGetSessionAsync(
        DBusConnection connection,
        string sessionId
    )
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: LoginService,
                path: ManagerPath,
                @interface: ManagerInterface,
                member: "GetSession",
                signature: "s"
            );
            writer.WriteString(sessionId);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, s_readObjectPath)
            .WaitAsync(s_callTimeout)
            .ConfigureAwait(false);
    }

    private static async Task<string> CallManagerForPathAsync(
        DBusConnection connection,
        string member,
        uint argument
    )
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: LoginService,
                path: ManagerPath,
                @interface: ManagerInterface,
                member: member,
                signature: "u"
            );
            writer.WriteUInt32(argument); // logind defines PID 0 as the calling process.
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, s_readObjectPath)
            .WaitAsync(s_callTimeout)
            .ConfigureAwait(false);
    }

    private static async Task<string> ResolveDisplaySessionPathAsync(DBusConnection connection)
    {
        // GetUser(uid) -> user object path; the user's Display property is the (id, path) of the
        // graphical session, which is what we must gate on for autostarted GUI apps.
        var userPath = await CallManagerForPathAsync(
                connection,
                "GetUser",
                LibcGetUid()
            )
            .ConfigureAwait(false);

        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: LoginService,
                path: userPath,
                @interface: PropertiesInterface,
                member: "Get",
                signature: "ss"
            );
            writer.WriteString("org.freedesktop.login1.User");
            writer.WriteString("Display");
            message = writer.CreateMessage();
        }

        var sessionPath = await connection.CallMethodAsync(message, s_readDisplaySessionPath)
            .WaitAsync(s_callTimeout)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            throw new InvalidOperationException(
                "logind user has no graphical Display session to gate on."
            );
        }

        return sessionPath;
    }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint LibcGetUid();

    private async Task<IDisposable> WatchPropertiesAsync(
        DBusConnection connection,
        string sessionPath
    )
    {
        return await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = LoginService,
                Interface = PropertiesInterface,
                Path = sessionPath,
                Member = "PropertiesChanged",
                Arg0 = SessionInterface
            },
            s_readPropertiesChanged,
            HandlePropertiesChanged,
            ObserverFlags.None,
            emitOnCapturedContext: false
        ).ConfigureAwait(false);
    }

    private async Task<IDisposable> WatchLockSignalAsync(
        DBusConnection connection,
        string sessionPath,
        string member,
        bool locked
    )
    {
        return await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = LoginService,
                Interface = SessionInterface,
                Path = sessionPath,
                Member = member
            },
            locked ? s_readLockSignal : s_readUnlockSignal,
            locked ? HandleLockSignal : HandleUnlockSignal,
            ObserverFlags.None,
            emitOnCapturedContext: false
        ).ConfigureAwait(false);
    }

    private async Task RefreshPropertiesAsync(DBusConnection connection, string sessionPath)
    {
        while (true)
        {
            long version;
            lock (_lock)
            {
                if (_disposed || _fallbackAllowed)
                {
                    return;
                }

                version = _stateVersion;
            }

            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: LoginService,
                    path: sessionPath,
                    @interface: PropertiesInterface,
                    member: "GetAll",
                    signature: "s"
                );
                writer.WriteString(SessionInterface);
                message = writer.CreateMessage();
            }

            var properties = await connection.CallMethodAsync(message, s_readProperties)
                .WaitAsync(s_callTimeout)
                .ConfigureAwait(false);
            if (
                !properties.TryGetValue("Active", out var activeValue)
                || !properties.TryGetValue("LockedHint", out var lockedValue)
            )
            {
                throw new InvalidOperationException(
                    "logind session did not expose Active and LockedHint."
                );
            }

            EventHandler? changed;
            lock (_lock)
            {
                if (_disposed || _fallbackAllowed)
                {
                    return;
                }

                if (_stateVersion != version)
                {
                    continue;
                }

                _active = activeValue.GetBool();
                _lockedHint = lockedValue.GetBool();
                changed = UpdateAllowed_NoLock();
            }

            RaiseChanged(changed);
            return;
        }
    }

    private void HandlePropertiesChanged(
        Exception? exception,
        SessionPropertiesChanged change,
        object? readerState,
        object? handlerState
    )
    {
        if (exception is not null)
        {
            ScheduleUnavailableFallback(exception);
            return;
        }

        if (!string.Equals(change.InterfaceName, SessionInterface, StringComparison.Ordinal))
        {
            return;
        }

        bool? active = change.Changed.TryGetValue("Active", out var activeValue)
            ? activeValue.GetBool()
            : null;
        bool? locked = change.Changed.TryGetValue("LockedHint", out var lockedValue)
            ? lockedValue.GetBool()
            : null;
        var activeInvalidated = change.Invalidated.Contains("Active", StringComparer.Ordinal);
        var lockedInvalidated = change.Invalidated.Contains(
            "LockedHint",
            StringComparer.Ordinal
        );

        // Fail closed while an invalidated security property is refreshed.
        ApplySignalState(activeInvalidated ? false : active, lockedInvalidated ? true : locked);
        if (activeInvalidated || lockedInvalidated)
        {
            QueueRefresh();
        }
    }

    private void HandleLockSignal(
        Exception? exception,
        bool locked,
        object? readerState,
        object? handlerState
    )
    {
        if (exception is not null)
        {
            ScheduleUnavailableFallback(exception);
            return;
        }

        // Lock is an authoritative fail-closed hint: block immediately.
        ApplySignalState(null, true);
    }

    private void HandleUnlockSignal(
        Exception? exception,
        bool locked,
        object? readerState,
        object? handlerState
    )
    {
        if (exception is not null)
        {
            ScheduleUnavailableFallback(exception);
            return;
        }

        // login1's Unlock is only a request to the locker, not proof LockedHint cleared.
        // Never allow input off the signal itself; re-read the authoritative property and
        // let Active && !LockedHint govern. Stay blocked until that read confirms unlock.
        QueueRefresh();
    }

    private void ApplySignalState(bool? active, bool? lockedHint)
    {
        EventHandler? changed;
        lock (_lock)
        {
            if (_disposed || _fallbackAllowed)
            {
                return;
            }

            _stateVersion++;
            _active = active ?? _active;
            _lockedHint = lockedHint ?? _lockedHint;
            changed = UpdateAllowed_NoLock();
        }

        RaiseChanged(changed);
    }

    private void QueueRefresh()
    {
        DBusConnection? connection;
        string? sessionPath;
        lock (_lock)
        {
            connection = _connection;
            sessionPath = _sessionPath;
        }

        if (connection is null || sessionPath is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshPropertiesAsync(connection, sessionPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ScheduleUnavailableFallback(ex);
            }
        });
    }

    private EventHandler? UpdateAllowed_NoLock()
    {
        var allowed = DeriveInputAllowed(_active, _lockedHint);
        if (_isInputAllowed == allowed)
        {
            return null;
        }

        _isInputAllowed = allowed;
        return InputAllowedChanged;
    }

    private void ScheduleUnavailableFallback(Exception exception)
    {
        if (Interlocked.Exchange(ref _failureScheduled, 1) == 0)
        {
            _ = Task.Run(() => EnterUnavailableFallback(exception));
        }
    }

    private void EnterUnavailableFallback(Exception exception)
    {
        EventHandler? changed;
        Transport transport;
        bool failClosed;
        lock (_lock)
        {
            if (_disposed || _fallbackAllowed)
            {
                return;
            }

            // Once monitoring was established, any fault fails closed; before that, only a clean
            // absence signal (see IndicatesLogindAbsent) keeps the legacy fail-open behavior.
            failClosed = _established || !IndicatesLogindAbsent(exception);
            _fallbackAllowed = true;
            _active = !failClosed;
            _lockedHint = failClosed;
            _stateVersion++;
            changed = UpdateAllowed_NoLock();
            transport = DetachTransport_NoLock();
        }

        DisposeTransport(transport);
        if (Interlocked.Exchange(ref _loggedUnavailable, 1) == 0)
        {
            Trace.WriteLine(
                failClosed
                    ? $"[SessionActivityMonitor] logind monitoring lost after init; evdev input blocked: {exception.Message}"
                    : $"[SessionActivityMonitor] logind lock gating unavailable; evdev input remains enabled: {exception.Message}"
            );
        }

        RaiseChanged(changed);
    }

    private Transport DetachTransport_NoLock()
    {
        var transport = new Transport(_connection, _subscriptions.ToArray());
        _connection = null;
        _sessionPath = null;
        _subscriptions.Clear();
        return transport;
    }

    private static void DisposeTransport(Transport transport)
    {
        foreach (var subscription in transport.Subscriptions)
        {
            DisposeTransportPart(subscription);
        }

        DisposeTransportPart(transport.Connection);
    }

    private static void DisposeTransportPart(IDisposable? transportPart)
    {
        try
        {
            transportPart?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SessionActivityMonitor] D-Bus teardown threw: {ex.Message}");
        }
    }

    private void RaiseChanged(EventHandler? changed)
    {
        if (changed is null)
        {
            return;
        }

        try
        {
            changed(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[SessionActivityMonitor] InputAllowedChanged handler threw: {ex.Message}"
            );
        }
    }

    private sealed record SessionPropertiesChanged(
        string InterfaceName,
        Dictionary<string, VariantValue> Changed,
        List<string> Invalidated
    );

    private readonly record struct Transport(
        DBusConnection? Connection,
        IReadOnlyList<IDisposable> Subscriptions
    );
}
