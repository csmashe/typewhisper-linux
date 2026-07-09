using System.Diagnostics;
using Tmds.DBus.Protocol;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Persistent, event-driven client for the AT-SPI accessibility bus, backed by
///     the managed <c>Tmds.DBus.Protocol</c> binding. It registers for
///     <c>object:state-changed:focused</c> and <c>object:text-changed</c> signals and
///     raises .NET events carrying the source element's (bus name, object path). Unlike
///     <see cref="AtSpiUrlExtractor" /> — which spawns a <c>busctl</c> process per read
///     and is only invoked on demand — this holds one connection open for the app's
///     lifetime so focus/edit events arrive with no polling.
///     <para>
///         Everything is best-effort: if the a11y bus is unreachable (headless, minimal,
///         or remote sessions) <see cref="EnsureStartedAsync" /> returns <c>false</c> and
///         the feature that depends on it simply no-ops.
///     </para>
/// </summary>
public sealed class AtSpiEventClient : IAtSpiEventClient, IDisposable
{
    // ReSharper disable once InconsistentNaming -- "a11y" is the standard accessibility numeronym (a + 11 letters + y) mirroring the org.a11y.Bus service name; ReSharper's PascalCase splitter mis-reads "11y" and wants the non-standard "A11Y".
    private const string A11yBusName = "org.a11y.Bus";
    // ReSharper disable once InconsistentNaming -- see A11yBusName; keep the "a11y" numeronym mirroring the bus path.
    private const string A11yBusPath = "/org/a11y/bus";
    // ReSharper disable once InconsistentNaming -- see A11yBusName; keep the "a11y" numeronym mirroring the bus interface.
    private const string A11yBusInterface = "org.a11y.Bus";

    private const string RegistryBusName = "org.a11y.atspi.Registry";
    private const string RegistryPath = "/org/a11y/atspi/registry";
    private const string RegistryInterface = "org.a11y.atspi.Registry";

    private const string EventObjectInterface = "org.a11y.atspi.Event.Object";
    private const string TextInterface = "org.a11y.atspi.Text";
    private const string AccessibleInterface = "org.a11y.atspi.Accessible";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private const string FocusedStateName = "focused";
    private const int StateGained = 1;

    // AtspiRole.PASSWORD_TEXT — confirmed against the existing extractor's role numbering
    // (ROLE_FRAME = 23) which shares the same AtspiRole enum.
    private const uint RolePasswordText = 40;

    private static readonly MessageValueReader<string> s_readString =
        static (m, _) => m.GetBodyReader().ReadString();

    private static readonly MessageValueReader<int> s_readVariantInt32 =
        static (m, _) => m.GetBodyReader().ReadVariantValue().GetInt32();

    private static readonly MessageValueReader<uint> s_readUInt32 =
        static (m, _) => m.GetBodyReader().ReadUInt32();

    // Reads the leading (detail: string, detail1: int) of an AT-SPI event body
    // (full signature "siiv(so)"); the source element is taken from the message
    // header (sender + path), matching how libatspi/pyatspi derive event.source.
    private static readonly MessageValueReader<AtSpiSignal> s_readSignal =
        static (m, _) =>
        {
            var reader = m.GetBodyReader();
            var detail = reader.ReadString();
            var detail1 = reader.ReadInt32();
            return new AtSpiSignal(
                m.SenderAsString ?? string.Empty,
                m.PathAsString ?? string.Empty,
                detail,
                detail1
            );
        };

    private readonly IErrorLogService _errorLog;
    private readonly Lock _focusLock = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private AtSpiElementRef? _currentFocused;
    private DBusConnection? _connection;
    private bool _available;
    private bool _disposed;
    private bool _loggedUnavailable;
    private bool _started;
    private IDisposable? _stateSubscription;
    private IDisposable? _textSubscription;

    public AtSpiEventClient(IErrorLogService errorLog)
    {
        _errorLog = errorLog;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _stateSubscription?.Dispose();
            _textSubscription?.Dispose();
            _connection?.Dispose();
        }
        catch
        {
            // best effort — teardown of a dying bus connection must not throw.
        }

        _startGate.Dispose();
    }

    public event Action<AtSpiElementRef>? FocusChanged;
    public event Action<AtSpiElementRef>? TextChanged;

    public AtSpiElementRef? CurrentFocusedElement
    {
        get
        {
            lock (_focusLock)
            {
                return _currentFocused;
            }
        }
    }

    public bool IsRunning => _available;

    public async Task<bool> EnsureStartedAsync()
    {
        if (_started)
        {
            return _available;
        }

        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return _available;
            }

            var started = await TryStartAsync().ConfigureAwait(false);
            _available = started;
            // Only cache success. On failure TryStartAsync has already torn down any partial
            // connection, so leaving _started false lets a later call retry (e.g. the a11y bus
            // became available, or a transient connect error cleared).
            _started = started;
            return started;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                _stateSubscription?.Dispose();
                _textSubscription?.Dispose();
                _connection?.Dispose();
            }
            catch
            {
                // best effort — teardown of a dying bus connection must not throw.
            }

            _stateSubscription = null;
            _textSubscription = null;
            _connection = null;
            // Reset so the next EnsureStartedAsync reconnects fresh rather than returning
            // the stale cached availability.
            _started = false;
            _available = false;

            lock (_focusLock)
            {
                _currentFocused = null;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<string?> TryReadTextAsync(AtSpiElementRef element, int maxLength)
    {
        var conn = _connection;
        if (conn is null || !element.IsValid || maxLength <= 0)
        {
            return null;
        }

        try
        {
            var characterCount = await GetCharacterCountAsync(conn, element).ConfigureAwait(false);
            if (characterCount <= 0)
            {
                return null;
            }

            var end = Math.Min(characterCount, maxLength);
            return await GetTextAsync(conn, element, 0, end).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOnce($"AT-SPI text read failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool?> IsPasswordFieldAsync(AtSpiElementRef element)
    {
        var conn = _connection;
        if (conn is null || !element.IsValid)
        {
            // Cannot determine the role → indeterminate, not "safe". This is a privacy
            // boundary, so the caller must fail closed rather than proceed to read text.
            return null;
        }

        try
        {
            MessageBuffer message;
            using (var writer = conn.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: element.BusName,
                    path: element.ObjectPath,
                    @interface: AccessibleInterface,
                    member: "GetRole"
                );
                message = writer.CreateMessage();
            }

            var role = await conn.CallMethodAsync(message, s_readUInt32).ConfigureAwait(false);
            return role == RolePasswordText;
        }
        catch
        {
            // Role read failed (denied / transient / toolkit without a reliable role). Return
            // indeterminate so the caller skips rather than risk reading a password field.
            return null;
        }
    }

    private async Task<bool> TryStartAsync()
    {
        try
        {
            var address = await ResolveA11yBusAddressAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(address))
            {
                LogOnce("AT-SPI event client: a11y bus address unavailable.");
                return false;
            }

            var conn = new DBusConnection(address);
            await conn.ConnectAsync().ConfigureAwait(false);
            _connection = conn;

            _stateSubscription = await conn.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = EventObjectInterface,
                    Member = "StateChanged",
                    // The AT-SPI event detail (focused/showing/visible/checked/…) is the first
                    // body arg, so Arg0="focused" lets the bus daemon filter to focus changes
                    // for us instead of waking us for every state change session-wide. The
                    // in-handler detail/detail1 checks below stay as defense in depth.
                    Arg0 = FocusedStateName
                },
                s_readSignal,
                HandleStateChanged,
                ObserverFlags.None,
                emitOnCapturedContext: false
            ).ConfigureAwait(false);

            _textSubscription = await conn.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = EventObjectInterface,
                    Member = "TextChanged"
                },
                s_readSignal,
                HandleTextChanged,
                ObserverFlags.None,
                emitOnCapturedContext: false
            ).ConfigureAwait(false);

            // Tell registryd which events have listeners so toolkits that gate
            // event emission on demand (GTK) actually broadcast them.
            await RegisterEventAsync(conn, "object:state-changed:focused").ConfigureAwait(false);
            await RegisterEventAsync(conn, "object:text-changed").ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            LogOnce($"AT-SPI event client start failed: {ex.Message}");
            // A connection may have been made before AddMatchAsync/RegisterEventAsync threw.
            // Tear down any partial state so we don't leak a live connection/match, and so the
            // next EnsureStartedAsync retries from a clean slate.
            try
            {
                _stateSubscription?.Dispose();
                _textSubscription?.Dispose();
                _connection?.Dispose();
            }
            catch
            {
                // best effort — teardown of a half-open connection must not throw.
            }

            _stateSubscription = null;
            _textSubscription = null;
            _connection = null;
            return false;
        }
    }

    private void HandleStateChanged(Exception? exception, AtSpiSignal signal, object? readerState, object? handlerState)
    {
        // Only successful reads carry a signal. On error/disconnect the observer is invoked
        // with a non-null exception and a default value; skip those rather than acting on an
        // empty AtSpiSignal.
        if (exception is not null)
        {
            return;
        }

        if (
            !string.Equals(signal.Detail, FocusedStateName, StringComparison.Ordinal)
            || signal.Detail1 != StateGained
        )
        {
            return;
        }

        var element = new AtSpiElementRef(signal.Sender, signal.Path);
        if (!element.IsValid)
        {
            return;
        }

        lock (_focusLock)
        {
            _currentFocused = element;
        }

        // A subscriber throwing here runs on the D-Bus dispatch thread and would fault the
        // connection, killing all future events. Isolate it.
        try
        {
            FocusChanged?.Invoke(element);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiEventClient] FocusChanged subscriber threw: {ex.Message}");
        }
    }

    private void HandleTextChanged(Exception? exception, AtSpiSignal signal, object? readerState, object? handlerState)
    {
        if (exception is not null)
        {
            return;
        }

        var element = new AtSpiElementRef(signal.Sender, signal.Path);
        if (!element.IsValid)
        {
            return;
        }

        try
        {
            TextChanged?.Invoke(element);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiEventClient] TextChanged subscriber threw: {ex.Message}");
        }
    }

    // ReSharper disable once InconsistentNaming -- "a11y" is the standard accessibility numeronym mirroring org.a11y.Bus; ReSharper's PascalCase splitter mis-reads "11y".
    private async Task<string?> ResolveA11yBusAddressAsync()
    {
        try
        {
            var session = DBusConnection.Session;
            MessageBuffer message;
            using (var writer = session.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: A11yBusName,
                    path: A11yBusPath,
                    @interface: A11yBusInterface,
                    member: "GetAddress"
                );
                message = writer.CreateMessage();
            }

            return await session.CallMethodAsync(message, s_readString).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOnce($"AT-SPI GetAddress failed: {ex.Message}");
            return null;
        }
    }

    private static async Task RegisterEventAsync(DBusConnection conn, string eventName)
    {
        MessageBuffer message;
        using (var writer = conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: RegistryBusName,
                path: RegistryPath,
                @interface: RegistryInterface,
                member: "RegisterEvent",
                signature: "s"
            );
            writer.WriteString(eventName);
            message = writer.CreateMessage();
        }

        await conn.CallMethodAsync(message).ConfigureAwait(false);
    }

    private static async Task<int> GetCharacterCountAsync(DBusConnection conn, AtSpiElementRef element)
    {
        MessageBuffer message;
        using (var writer = conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: element.BusName,
                path: element.ObjectPath,
                @interface: PropertiesInterface,
                member: "Get",
                signature: "ss"
            );
            writer.WriteString(TextInterface);
            writer.WriteString("CharacterCount");
            message = writer.CreateMessage();
        }

        return await conn.CallMethodAsync(message, s_readVariantInt32).ConfigureAwait(false);
    }

    private static async Task<string?> GetTextAsync(
        DBusConnection conn,
        AtSpiElementRef element,
        int start,
        int end
    )
    {
        MessageBuffer message;
        using (var writer = conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: element.BusName,
                path: element.ObjectPath,
                @interface: TextInterface,
                member: "GetText",
                signature: "ii"
            );
            writer.WriteInt32(start);
            writer.WriteInt32(end);
            message = writer.CreateMessage();
        }

        return await conn.CallMethodAsync(message, s_readString).ConfigureAwait(false);
    }

    private void LogOnce(string message)
    {
        Trace.WriteLine($"[AtSpiEventClient] {message}");
        if (_loggedUnavailable)
        {
            return;
        }

        _loggedUnavailable = true;
        _errorLog.AddEntry(message, ErrorCategory.Detection);
    }

    private readonly record struct AtSpiSignal(string Sender, string Path, string Detail, int Detail1);
}
