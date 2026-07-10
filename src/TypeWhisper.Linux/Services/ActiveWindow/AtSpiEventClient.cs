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
    private const string RegistryRootPath = "/org/a11y/atspi/accessible/root";

    // How many top-level children (windows) of each application root get the Chromium
    // unlock poke; one window is the normal case, a few more cover multi-window apps.
    private const int MaxPokedChildren = 4;

    private const string EventObjectInterface = "org.a11y.atspi.Event.Object";
    private const string TextInterface = "org.a11y.atspi.Text";
    private const string AccessibleInterface = "org.a11y.atspi.Accessible";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private const string FocusedStateName = "focused";
    private const int StateGained = 1;

    // LibreOffice Writer alternates focus between the caret paragraph and the document's
    // root pane, so two entries would suffice there; a few more absorb apps that flap
    // across additional structural nodes.
    private const int RecentFocusCapacity = 8;

    // AtspiRole.PASSWORD_TEXT — confirmed against the existing extractor's role numbering
    // (ROLE_FRAME = 23) which shares the same AtspiRole enum.
    private const uint RolePasswordText = 40;

    private static readonly MessageValueReader<string> s_readString =
        static (m, _) => m.GetBodyReader().ReadString();

    private static readonly MessageValueReader<int> s_readVariantInt32 =
        static (m, _) => m.GetBodyReader().ReadVariantValue().GetInt32();

    private static readonly MessageValueReader<uint> s_readUInt32 =
        static (m, _) => m.GetBodyReader().ReadUInt32();

    // Reads a D-Bus a(so) array — the (unique bus name, object path) pairs the registry
    // and Accessible.GetChildren return.
    private static readonly MessageValueReader<List<AtSpiElementRef>> s_readElementRefArray =
        static (m, _) =>
        {
            var reader = m.GetBodyReader();
            var list = new List<AtSpiElementRef>();
            var end = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(end))
            {
                var bus = reader.ReadString();
                var path = reader.ReadObjectPath().ToString();
                list.Add(new AtSpiElementRef(bus, path));
            }

            return list;
        };

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

    // Bounded most-recent-first focus history behind _focusLock; see GetRecentFocusedElements.
    private readonly List<AtSpiElementRef> _recentFocused = [];

    // Unique bus names already sent the Chromium web-content unlock poke on the current
    // connection (unique names are never recycled within a bus lifetime). Behind _focusLock.
    private readonly HashSet<string> _pokedApps = [];

    private AtSpiElementRef? _currentFocused;
    private DBusConnection? _connection;
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

    public IReadOnlyList<AtSpiElementRef> GetRecentFocusedElements()
    {
        lock (_focusLock)
        {
            return [.. _recentFocused];
        }
    }

    public bool IsRunning { get; private set; }

    public async Task<bool> EnsureStartedAsync()
    {
        if (_started)
        {
            return IsRunning;
        }

        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return IsRunning;
            }

            var started = await TryStartAsync().ConfigureAwait(false);
            IsRunning = started;
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
            IsRunning = false;

            lock (_focusLock)
            {
                _currentFocused = null;
                _recentFocused.Clear();
                // Unique names may outlive our connection, but a fresh connection re-pokes
                // cheaply and correctly (apps keep their unlocked state anyway).
                _pokedApps.Clear();
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
            // Reading the focused field is best-effort: many targets simply don't implement the
            // AT-SPI Text interface (terminals, TUIs, Claude Code), or the accessible disappears
            // between the focus signal and the read. Those surface as benign D-Bus errors and mean
            // "can't learn from this app" — not a TypeWhisper fault — so keep them out of the
            // user-facing error log (Trace only). Genuinely unexpected failures still log once.
            if (IsExpectedUnreadableTarget(ex))
            {
                Trace.WriteLine(
                    $"[AtSpiEventClient] AT-SPI text read skipped (target has no readable text): {ex.Message}"
                );
            }
            else
            {
                LogOnce($"AT-SPI text read failed: {ex.Message}");
            }

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

    public async Task PokeAccessibilityTreesAsync()
    {
        var conn = _connection;
        if (conn is null)
        {
            return;
        }

        try
        {
            var apps = await GetChildrenAsync(conn, RegistryBusName, RegistryRootPath)
                .ConfigureAwait(false);
            foreach (var app in apps)
            {
                bool alreadyPoked;
                lock (_focusLock)
                {
                    alreadyPoked = !_pokedApps.Add(app.BusName);
                }

                if (alreadyPoked)
                {
                    continue;
                }

                // Fire-and-forget per app: a hung target must not stall the sweep. The
                // optimistic Add above dedupes concurrent sweeps; on failure we un-mark the
                // app so a later arm retries it — a transient D-Bus error must never leave an
                // app permanently skipped with its tree still locked.
                _ = PokeAppAndTrackAsync(conn, app);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiEventClient] a11y app enumeration for poke failed: {ex.Message}");
        }
    }

    private async Task PokeAppAndTrackAsync(DBusConnection conn, AtSpiElementRef appRoot)
    {
        if (!await PokeAppAsync(conn, appRoot).ConfigureAwait(false))
        {
            lock (_focusLock)
            {
                _pokedApps.Remove(appRoot.BusName);
            }
        }
    }

    // Chromium/Electron apps join the a11y bus (when org.a11y.Status.IsEnabled was true at
    // their launch) but expose only their application root and toplevel window until an
    // assistive tool calls GetAttributes or GetRelationSet on one of their nodes — that call
    // is Chromium's "a screen reader is actually reading me" signal and switches the web
    // content (editor) tree on. Touch the app root and its first few children; results are
    // discarded, only the calls matter. Harmless no-op for every other toolkit. Each node/call
    // is isolated (see TouchElementAsync), so one unavailable member or defunct child never
    // skips the rest. Returns false only when nothing reached the app at all, so the caller can
    // un-mark it for a later retry; a partial success still counts as poked.
    private static async Task<bool> PokeAppAsync(DBusConnection conn, AtSpiElementRef appRoot)
    {
        var anyTouched = await TouchElementAsync(conn, appRoot).ConfigureAwait(false);

        List<AtSpiElementRef> children;
        try
        {
            children = await GetChildrenAsync(conn, appRoot.BusName, appRoot.ObjectPath)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The app may have exited or not expose GetChildren; the root touch above may
            // still have unlocked it, so report whatever it achieved.
            Trace.WriteLine(
                $"[AtSpiEventClient] a11y child enumeration of {appRoot.BusName} failed: {ex.Message}"
            );
            return anyTouched;
        }

        foreach (var child in children.Take(MaxPokedChildren))
        {
            anyTouched |= await TouchElementAsync(conn, child).ConfigureAwait(false);
        }

        return anyTouched;
    }

    // Issues both Chromium unlock triggers (GetAttributes, GetRelationSet) on one node,
    // independently: one being unavailable or failing must not skip the other. Returns true if
    // at least one call succeeded.
    private static async Task<bool> TouchElementAsync(DBusConnection conn, AtSpiElementRef element)
    {
        var touched = false;
        foreach (var member in (string[])["GetAttributes", "GetRelationSet"])
        {
            try
            {
                MessageBuffer message;
                using (var writer = conn.GetMessageWriter())
                {
                    writer.WriteMethodCallHeader(
                        destination: element.BusName,
                        path: element.ObjectPath,
                        @interface: AccessibleInterface,
                        member: member
                    );
                    message = writer.CreateMessage();
                }

                await conn.CallMethodAsync(message).ConfigureAwait(false);
                touched = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[AtSpiEventClient] a11y {member} on {element.BusName} failed: {ex.Message}"
                );
            }
        }

        return touched;
    }

    private static async Task<List<AtSpiElementRef>> GetChildrenAsync(
        DBusConnection conn,
        string busName,
        string objectPath
    )
    {
        MessageBuffer message;
        using (var writer = conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: busName,
                path: objectPath,
                @interface: AccessibleInterface,
                member: "GetChildren"
            );
            message = writer.CreateMessage();
        }

        return await conn.CallMethodAsync(message, s_readElementRefArray).ConfigureAwait(false);
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
            _recentFocused.Remove(element);
            _recentFocused.Insert(0, element);
            if (_recentFocused.Count > RecentFocusCapacity)
            {
                _recentFocused.RemoveAt(RecentFocusCapacity);
            }
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

    // The D-Bus error name leads the reply exception's message, e.g.
    // "org.freedesktop.DBus.Error.InvalidArgs: No such interface ...". These names are stable
    // wire-protocol constants (not localized), so a prefix match on the guarded exception is safe.
    private static readonly string[] s_benignReadErrorNames =
    [
        "org.freedesktop.DBus.Error.InvalidArgs", // element has no Text interface
        "org.freedesktop.DBus.Error.UnknownObject", // accessible vanished after focus
        "org.freedesktop.DBus.Error.UnknownInterface",
        "org.freedesktop.DBus.Error.UnknownMethod",
        "org.freedesktop.DBus.Error.ServiceUnknown", // app's a11y bridge went away
        "org.freedesktop.DBus.Error.NoReply", // app busy / not responding
        "org.freedesktop.DBus.Error.Disconnected"
    ];

    // AT-SPI text reads run against whatever third-party app holds focus, so failure is expected,
    // not exceptional: terminals / TUIs / Claude Code don't implement org.a11y.atspi.Text, and an
    // accessible can vanish between the focus signal and the read. Tmds surfaces these as
    // DBusErrorReplyException; the well-known "not readable / gone / unresponsive" names are benign.
    private static bool IsExpectedUnreadableTarget(Exception ex)
    {
        if (ex is not DBusErrorReplyException)
        {
            return false;
        }

        var message = ex.Message;
        return Array.Exists(
            s_benignReadErrorNames,
            name => message.StartsWith(name, StringComparison.Ordinal)
        );
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
