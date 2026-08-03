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
    private const string ComponentInterface = "org.a11y.atspi.Component";
    private const string AccessibleInterface = "org.a11y.atspi.Accessible";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    // ATSPI_COORD_TYPE_SCREEN: GetExtents returns the element's box in global screen pixels
    // (not window- or parent-relative), which is what we need to place the toast beside it.
    private const uint CoordTypeScreen = 0;

    private const string FocusedStateName = "focused";
    private const int StateGained = 1;

    // AT-SPI event names driven through RegisterEvent/DeregisterEvent. focused is registered
    // permanently (focus tracking must run before any dictation arms); text-changed is
    // registered on demand only while a holder needs it — see AcquireTextChangedEvents.
    private const string FocusedEventName = "object:state-changed:focused";
    private const string TextChangedEventName = "object:text-changed";

    // A failed text-changed register/deregister leaves the tracked state unknown; retry a few times
    // with a short delay so a FINAL deregister (refcount back to 0, no following lease edge to
    // re-drive it) still converges rather than stranding the GTK text-event flood on.
    private const int MaxTextEventReconcileAttempts = 4;
    private static readonly TimeSpan s_textEventRetryDelay = TimeSpan.FromSeconds(1);

    // LibreOffice Writer alternates focus between the caret paragraph and the document's
    // root pane, so two entries would suffice there; a few more absorb apps that flap
    // across additional structural nodes.
    private const int RecentFocusCapacity = 8;

    // AtspiRole.PASSWORD_TEXT — confirmed against the existing extractor's role numbering
    // (ROLE_FRAME = 23) which shares the same AtspiRole enum.
    private const uint RolePasswordText = 40;

    // AtspiStateType bit positions within GetState's first bitfield word. ACTIVE marks a
    // toolkit's currently activated top-level window; FOCUSED the widget holding keyboard
    // focus. FOCUSED is 12 — 11 is FOCUSABLE, a classic off-by-one when reading the enum.
    private const int StateActiveBit = 1;
    private const int StateFocusedBit = 12;

    // Bounds for the cold-start focus bootstrap (TryBootstrapFocusAsync): how many top-level
    // windows per application get a state probe, and the breadth-first budget inside an
    // active window. Generous enough to reach a focused widget nested in structural panes
    // (LibreOffice Writer's caret paragraph sits ~4 levels down) while keeping the one-off
    // scan bounded against pathological trees.
    private const int MaxBootstrapWindowsPerApp = 8;
    private const int MaxBootstrapDepth = 10;
    private const int MaxBootstrapNodesPerWindow = 250;

    // How many FOCUSED elements one bootstrap seeds into the focus history. More than one
    // matters: a structural node can hold FOCUSED without any Text interface (LibreOffice's
    // document frame), so consumers need the sibling candidates too.
    private const int MaxBootstrapSeeds = 4;

    // One-shot calls target arbitrary third-party apps, and a hung target (stopped process,
    // busy main loop) would otherwise pin the awaiting task forever — Tmds.DBus 0.92 applies
    // no timeout of its own — stalling the serialized commit chain with it. WaitAsync leaves
    // the pending reply entry behind until the reply or a disconnect arrives; that is
    // bounded and far preferable to an unbounded stall.
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(4);

    // Hard ceiling on one cold-start focus walk. Every underlying call is already bounded by
    // s_callTimeout, but a degraded tree with hundreds of slow-but-not-hung nodes could sum
    // those per-call timeouts into minutes and stall the arm long after the correction happened.
    // When it trips, the arm proceeds without focus and the next dictation retries.
    private static readonly TimeSpan s_bootstrapDeadline = TimeSpan.FromSeconds(5);

    private static readonly MessageValueReader<string> s_readString =
        static (m, _) => m.GetBodyReader().ReadString();

    private static readonly MessageValueReader<int> s_readVariantInt32 =
        static (m, _) => m.GetBodyReader().ReadVariantValue().GetInt32();

    private static readonly MessageValueReader<uint> s_readUInt32 =
        static (m, _) => m.GetBodyReader().ReadUInt32();

    // Reads Accessible.GetState's "au" reply — two 32-bit words of AtspiStateType bits. Only
    // word 0 is needed (ACTIVE=1 and FOCUSED=12 both live there).
    private static readonly MessageValueReader<uint> s_readStateSetWord0 =
        static (m, _) =>
        {
            var reader = m.GetBodyReader();
            var end = reader.ReadArrayStart(DBusType.UInt32);
            uint word0 = 0;
            var index = 0;
            while (reader.HasNext(end))
            {
                var value = reader.ReadUInt32();
                if (index++ == 0)
                {
                    word0 = value;
                }
            }

            return word0;
        };

    // GetExtents' reply body is a single D-Bus struct "(iiii)" = (x, y, width, height) in screen
    // pixels. AlignStruct() advances to the 8-byte struct boundary (a no-op when the struct leads
    // the body, but correct regardless) before the four Int32 members are read in order.
    private static readonly MessageValueReader<AtSpiScreenRect> s_readExtents =
        static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.AlignStruct();
            var x = reader.ReadInt32();
            var y = reader.ReadInt32();
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            return new AtSpiScreenRect(x, y, width, height);
        };

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

    // Reads GetRegisteredEvents' a(ss) reply — (registrant unique name, event type) pairs. Used to
    // check whether a DeregisterEvent actually removed our text-changed listener (some registryd
    // versions acknowledge the call without removing it).
    private static readonly MessageValueReader<List<(string Sender, string EventType)>>
        s_readRegisteredEvents =
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                var list = new List<(string, string)>();
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    var sender = reader.ReadString();
                    var eventType = reader.ReadString();
                    list.Add((sender, eventType));
                }

                return list;
            };

    // Reads a NameOwnerChanged body (s name, s oldOwner, s newOwner); only the new owner
    // matters (empty = the name went away, non-empty = a new registryd took over).
    private static readonly MessageValueReader<string> s_readNameOwnerChanged =
        static (m, _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadString(); // name (already filtered by Arg0)
            reader.ReadString(); // old owner
            return reader.ReadString();
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

    // Guards the {_disposed, _started, IsRunning} triple and TearDownConnection's snapshot-and-null
    // of the connection/subscription fields. TryStartAsync writes them outside it: starts serialize
    // on _startGate, and a post-disposal publish is swept by EnsureStartedAsync's _disposed
    // re-check. Dispose can't use _startGate — it is synchronous and would stall mid-connect.
    private readonly Lock _lifecycleLock = new();

    // Guards _textChangedRefCount and _textChangedRegistered. A dedicated lock (not _focusLock) so
    // an acquire/release from a commit or paste path never contends with the focus-event fast path
    // on the dispatch thread.
    private readonly Lock _textEventLock = new();

    // Serializes the registry register/deregister of text-changed. Every edge (lease acquire /
    // release, reconnect, registryd restart) drives state through the single reconciler under this
    // gate, so transitions apply in one last-write-wins order — never as reversed fire-and-forget
    // D-Bus calls that could leave events off while a lease is live, or on with none live. See
    // ReconcileTextChangedAsync.
    private readonly SemaphoreSlim _textEventReconcileGate = new(1, 1);

    // Number of live AcquireTextChangedEvents leases. >0 means "object:text-changed" must be
    // registered with the registry whenever we hold a connection; 0 means it must not be. The
    // count OUTLIVES a StopAsync/reconnect (holders still expect events after a reconnect), so
    // the reconciler re-registers text-changed exactly when this is >0.
    private int _textChangedRefCount;

    // What the reconciler last drove the CURRENT connection to: true/false = known
    // registered/deregistered, null = UNKNOWN (a RegisterEvent/DeregisterEvent failed or timed out).
    // Guarded by _textEventLock; set false when the connection is torn down or a restarted registryd
    // forgets our registration. A known value keeps a steady-state paste cycle (refcount 1→2→1) a
    // no-op; null forces the next reconcile to re-drive, so a failure is retried and a timed-out-
    // then-succeeded call can't strand events on with no lease live.
    private bool? _textChangedRegistered;

    // Bounded most-recent-first focus history behind _focusLock; see GetRecentFocusedElements.
    private readonly List<AtSpiElementRef> _recentFocused = [];

    // Unique bus names already sent the Chromium web-content unlock poke on the current
    // connection (unique names are never recycled within a bus lifetime). Behind _focusLock.
    private readonly HashSet<string> _pokedApps = [];

    // Per-app poke tasks still running on the current connection, keyed by unique bus name, so
    // a second concurrent sweep joins the in-flight work instead of treating a not-yet-unlocked
    // app as done. Behind _focusLock; an entry moves to _pokedApps on success or is dropped on
    // failure so a later arm retries it.
    private readonly Dictionary<string, Task> _pokesInFlight = [];

    private AtSpiElementRef? _currentFocused;

    // In-flight cold-start focus scan, so concurrent bootstrap callers share one walk
    // instead of each traversing the bus. Behind _focusLock; cleared when the scan settles.
    private Task<AtSpiElementRef?>? _focusBootstrap;

    // Cancels the in-flight bootstrap: fires on its own deadline (s_bootstrapDeadline) and is
    // tripped by StopAsync so a scan started on a now-dead connection cannot seed focus from it.
    // Paired with _focusBootstrap and identity-checked so a settling old scan never clears a
    // newer connection's slot. Behind _focusLock.
    private CancellationTokenSource? _focusBootstrapCts;
    private DBusConnection? _connection;
    private bool _disposed;
    private bool _loggedUnavailable;
    private bool _started;
    private IDisposable? _stateSubscription;
    private IDisposable? _textSubscription;
    private IDisposable? _registryOwnerSubscription;

    // 1 once a signal observer reported a fatal connection error and a reset was scheduled;
    // back to 0 when a fresh connection starts. Ensures one reset per dead connection.
    private int _resetScheduled;

    public AtSpiEventClient(IErrorLogService errorLog)
    {
        _errorLog = errorLog;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Consumers that must not start the listeners themselves gate on IsRunning; leaving it
            // true after disposal would send them at a torn-down connection.
            _started = false;
            IsRunning = false;
        }

        TearDownConnection();

        // _startGate is deliberately NOT disposed: fire-and-forget reconciles and arms can still be
        // mid-wait at shutdown, and a disposed semaphore would fault their WaitAsync or their
        // finally's Release. SemaphoreSlim needs disposal only when its AvailableWaitHandle is used
        // — same rationale as TargetAppCorrectionLearningService's listen gate.
    }

    // The single teardown point, so the startup-failure, stop, disposal and
    // disposed-while-connecting paths can't drift apart. Detaches under the lock and disposes
    // outside it, so racing callers can't double-dispose or run bus teardown while holding it.
    private void TearDownConnection()
    {
        IDisposable? stateSubscription;
        IDisposable? textSubscription;
        IDisposable? registryOwnerSubscription;
        DBusConnection? connection;
        lock (_lifecycleLock)
        {
            stateSubscription = _stateSubscription;
            textSubscription = _textSubscription;
            registryOwnerSubscription = _registryOwnerSubscription;
            connection = _connection;
            _stateSubscription = null;
            _textSubscription = null;
            _registryOwnerSubscription = null;
            _connection = null;
        }

        try
        {
            stateSubscription?.Dispose();
            textSubscription?.Dispose();
            registryOwnerSubscription?.Dispose();
            connection?.Dispose();
        }
        catch
        {
            // best effort — teardown of a dying bus connection must not throw.
        }
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
        // Fast path only — the authoritative check happens under the gate below.
        if (_disposed)
        {
            return false;
        }

        if (_started)
        {
            return IsRunning;
        }

        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Dispose can land during the wait; connecting after its teardown would build a
            // connection nothing ever closes.
            if (_disposed)
            {
                return false;
            }

            if (_started)
            {
                return IsRunning;
            }

            var started = await TryStartAsync().ConfigureAwait(false);

            // TryStartAsync awaits, so Dispose may have swept past this brand-new connection while
            // it was being built. Test and publish together, or this overwrites its cleared state.
            lock (_lifecycleLock)
            {
                if (!_disposed)
                {
                    IsRunning = started;
                    // Only cache success. On failure TryStartAsync has already torn down any
                    // partial connection, so leaving _started false lets a later call retry
                    // (e.g. the a11y bus became available, or a transient connect error cleared).
                    _started = started;
                    return started;
                }
            }

            // Disposed while connecting: drop what we just built. Idempotent, so it's safe even
            // when Dispose's own teardown already claimed it.
            TearDownConnection();
            return false;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public IDisposable AcquireTextChangedEvents()
    {
        lock (_textEventLock)
        {
            _textChangedRefCount++;
        }

        // Fire-and-forget: registration must not block the caller (arm/paste paths are
        // latency-sensitive). The reconciler serializes this against every other edge and reads
        // the live refcount when it runs, so concurrent acquire/release can't be applied out of
        // order; a failure is benign (the registry drops our registrations on disconnect and a
        // missed one only degrades to the consumer's own timeout fallback).
        _ = ReconcileTextChangedAsync();

        return new TextChangedLease(this);
    }

    // Releases one text-changed lease. Idempotent per handle: the handle guards its own
    // double-dispose, so this runs at most once per acquire.
    private void ReleaseTextChangedEvents()
    {
        lock (_textEventLock)
        {
            if (_textChangedRefCount == 0)
            {
                // Defensive: a correct handle disposes exactly once, but never underflow.
                return;
            }

            _textChangedRefCount--;
        }

        _ = ReconcileTextChangedAsync();
    }

    // Drives the registry's text-changed registration to match the live lease count. Serialized on
    // _textEventReconcileGate so acquire/release edges, reconnects and registryd restarts apply
    // strictly in order: whichever reconcile runs last observes the final refcount and leaves the
    // registration matching it. _textChangedRegistered makes an unchanged desired state a no-op.
    private async Task ReconcileTextChangedAsync(int attempt = 0)
    {
        bool failed;
        await _textEventReconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool desired;
            DBusConnection? conn;
            lock (_textEventLock)
            {
                desired = _textChangedRefCount > 0;
                conn = _connection;
                if (conn is null || _textChangedRegistered == desired)
                {
                    // No connection yet (TryStartAsync registers from the refcount on connect), or
                    // the registration is KNOWN to already match what we want. A null (unknown)
                    // state never equals desired, so a prior failure always re-drives here.
                    return;
                }
            }

            bool? outcome;
            if (desired)
            {
                outcome = await RegisterTextChangedAsync(conn).ConfigureAwait(false)
                    ? true
                    : null;
            }
            else if (await DeregisterTextChangedAsync(conn).ConfigureAwait(false))
            {
                // Trust-but-verify: an AT-SPI Registry v2 registryd (at-spi2-core 2.60.4) ACKs
                // DeregisterEvent without actually removing the listener. Re-read the registry for
                // the real state, so a no-op deregister is recorded as still-registered and the next
                // acquire skips re-registering — otherwise a duplicate stacks on every lease cycle,
                // recreating the text-event flood this path exists to prevent.
                try
                {
                    outcome = await IsTextChangedRegisteredAsync(conn).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[AtSpiEventClient] deregister verify failed: {ex.Message}");
                    outcome = null;
                }
            }
            else
            {
                outcome = null;
            }

            lock (_textEventLock)
            {
                // Record the real state; null (a call failed / couldn't verify) re-drives next edge.
                // Never assume a register/deregister took effect (see _textChangedRegistered).
                if (ReferenceEquals(_connection, conn))
                {
                    _textChangedRegistered = outcome;
                }
            }

            // Retry only when the state is genuinely unknown. A verified v2 no-op deregister is a
            // KNOWN "still registered" state, not a failure — retrying could never remove it.
            failed = outcome is null;
        }
        finally
        {
            _textEventReconcileGate.Release();
        }

        if (failed && attempt + 1 < MaxTextEventReconcileAttempts && !_disposed)
        {
            _ = RetryReconcileTextChangedAsync(attempt + 1);
        }
    }

    // A register/deregister failed, leaving the state unknown. A later lease edge would re-drive it,
    // but the final deregister may have none, so retry after a short delay to guarantee convergence
    // (its own failure schedules the next attempt, up to MaxTextEventReconcileAttempts).
    private async Task RetryReconcileTextChangedAsync(int attempt)
    {
        try
        {
            await Task.Delay(s_textEventRetryDelay).ConfigureAwait(false);
            if (!_disposed)
            {
                await ReconcileTextChangedAsync(attempt).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[AtSpiEventClient] text-changed reconcile retry failed: {ex.Message}"
            );
        }
    }

    private static async Task<bool> RegisterTextChangedAsync(DBusConnection conn)
    {
        try
        {
            await RegisterEventAsync(conn, TextChangedEventName).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiEventClient] text-changed RegisterEvent failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> DeregisterTextChangedAsync(DBusConnection conn)
    {
        try
        {
            await DeregisterEventAsync(conn, TextChangedEventName).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Benign: registryd also drops our registrations when this connection disconnects,
            // so a failed deregister just means it happens slightly later or on disconnect.
            Trace.WriteLine(
                $"[AtSpiEventClient] text-changed DeregisterEvent failed: {ex.Message}"
            );
            return false;
        }
    }

    public async Task StopAsync()
    {
        // Dispose already tore the connection down; a late reconcile or observer-error reset has
        // nothing left to stop.
        if (_disposed)
        {
            return;
        }

        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TearDownConnection();
            // Reset so the next EnsureStartedAsync reconnects fresh rather than returning
            // the stale cached availability.
            _started = false;
            IsRunning = false;

            lock (_focusLock)
            {
                _currentFocused = null;
                _recentFocused.Clear();
                // A scan still in flight ran against the dead connection; cancel it (so it can't
                // seed focus from a torn-down element) and drop it so the next bootstrap on a
                // fresh connection starts its own walk instead of joining it.
                _focusBootstrapCts?.Cancel();
                _focusBootstrap = null;
                _focusBootstrapCts = null;
                // Unique names may outlive our connection, but a fresh connection re-pokes
                // cheaply and correctly (apps keep their unlocked state anyway). In-flight pokes
                // against the dead connection settle on their own; drop their bookkeeping.
                _pokedApps.Clear();
                _pokesInFlight.Clear();
            }

            // Deliberately NOT touching _textChangedRefCount: the registration dies with the
            // connection, but holders still exist and expect events after a reconnect, so the
            // next EnsureStartedAsync/TryStartAsync re-registers text-changed when the count > 0.
            // Do clear _textChangedRegistered: this connection's registration is gone, so the next
            // connect must re-drive it from the surviving refcount rather than treat it as applied.
            lock (_textEventLock)
            {
                _textChangedRegistered = false;
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

            var role = await conn.CallMethodAsync(message, s_readUInt32)
                .WaitAsync(s_callTimeout)
                .ConfigureAwait(false);
            return role == RolePasswordText;
        }
        catch
        {
            // Role read failed (denied / transient / toolkit without a reliable role). Return
            // indeterminate so the caller skips rather than risk reading a password field.
            return null;
        }
    }

    public async Task<AtSpiScreenRect?> TryGetScreenExtentsAsync(AtSpiElementRef element)
    {
        var conn = _connection;
        if (conn is null || !element.IsValid)
        {
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
                    @interface: ComponentInterface,
                    member: "GetExtents",
                    signature: "u"
                );
                writer.WriteUInt32(CoordTypeScreen);
                message = writer.CreateMessage();
            }

            return await conn.CallMethodAsync(message, s_readExtents)
                .WaitAsync(s_callTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Extents are only for positioning feedback UI: many targets don't implement the
            // Component interface (or the accessible vanished), and the caller falls back to a
            // fixed on-screen spot — so keep every failure out of the error log (Trace only).
            Trace.WriteLine($"[AtSpiEventClient] GetExtents failed: {ex.Message}");
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
            var pokes = new List<Task>();
            foreach (var app in apps)
            {
                Task pokeTask;
                lock (_focusLock)
                {
                    // Bail if a reset swapped the connection out from under this sweep: StopAsync
                    // clears _pokesInFlight under this same lock, so inserting here afterwards
                    // would orphan an entry its connection-guarded finally then refuses to remove,
                    // permanently blocking that app's poke on the fresh connection. This check and
                    // the insert below share one lock acquisition, so they can't straddle a clear.
                    if (!ReferenceEquals(_connection, conn))
                    {
                        break;
                    }

                    if (_pokedApps.Contains(app.BusName))
                    {
                        // Already unlocked on this connection; nothing left to wait for.
                        continue;
                    }

                    // Launched concurrently per app: a hung target must not stall the others'
                    // pokes. The first sweep to reach an app starts its poke; a concurrent sweep
                    // joins that same in-flight task rather than skipping the app, so its WhenAll
                    // can't return before the tree is actually unlocked. On failure the app is
                    // dropped from both sets so a later arm retries it — a transient D-Bus error
                    // must never leave an app permanently skipped with its tree still locked.
                    if (!_pokesInFlight.TryGetValue(app.BusName, out var existing))
                    {
                        existing = PokeAppAndTrackAsync(conn, app);
                        _pokesInFlight[app.BusName] = existing;
                    }

                    pokeTask = existing;
                }

                pokes.Add(pokeTask);
            }

            // Awaited in aggregate (every underlying call is time-bounded) so this task's
            // completion means the sweep is done — including in-flight pokes started by a
            // concurrent sweep — before the cold-start focus bootstrap scans a first-contact
            // app's freshly unlocked tree.
            await Task.WhenAll(pokes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiEventClient] a11y app enumeration for poke failed: {ex.Message}");
        }
    }

    private async Task PokeAppAndTrackAsync(DBusConnection conn, AtSpiElementRef appRoot)
    {
        // Force asynchronous completion so the caller's publish into _pokesInFlight lands
        // BEFORE this task can finish: a poke that failed synchronously (connection
        // disposed right after enumeration) would otherwise be stored already-completed, and
        // later sweeps would join that dead task forever instead of retrying the still-locked
        // tree. The finally then always clears the slot so a failure is retried.
        await Task.Yield();
        var poked = false;
        try
        {
            poked = await PokeAppAsync(conn, appRoot).ConfigureAwait(false);
        }
        finally
        {
            lock (_focusLock)
            {
                // Only touch bookkeeping if this poke's connection is still current: a reset may
                // have swapped connections (StopAsync already cleared both sets) and even
                // re-added the same bus name for a fresh poke — removing or promoting that on the
                // strength of this dead connection's work would let a cold sweep return before
                // the new tree is unlocked.
                if (ReferenceEquals(_connection, conn))
                {
                    // Leave the in-flight set the moment work finishes; promote to _pokedApps
                    // only on success so a failure (transient D-Bus error, app gone) is retried
                    // by a later arm rather than recorded as unlocked.
                    _pokesInFlight.Remove(appRoot.BusName);
                    if (poked)
                    {
                        _pokedApps.Add(appRoot.BusName);
                    }
                }
            }
        }
    }

    public Task<AtSpiElementRef?> TryBootstrapFocusAsync()
    {
        var conn = _connection;
        if (conn is null)
        {
            return Task.FromResult<AtSpiElementRef?>(null);
        }

        lock (_focusLock)
        {
            if (_currentFocused is { } known)
            {
                // A real focus event has been seen; the event path is authoritative.
                return Task.FromResult<AtSpiElementRef?>(known);
            }

            // Concurrent callers join the in-flight scan rather than each walking the bus.
            if (_focusBootstrap is { } inFlight)
            {
                return inFlight;
            }

            var cts = new CancellationTokenSource(s_bootstrapDeadline);
            _focusBootstrapCts = cts;
            return _focusBootstrap = BootstrapFocusAsync(conn, cts);
        }
    }

    // Background startup seed: poke first, THEN scan. A Chromium/Electron target that is already
    // focused at connect exposes only a toplevel-window stub until poked, so scanning first would
    // seed that stub as authoritative focus and the cold-start arm — seeing non-null focus —
    // would take the warm path and never reveal the real editor. Poking before the walk lets it
    // find the actual field. Fully background: connecting must stay fast, and the poke sweep
    // joins any concurrent first-arm sweep rather than duplicating it.
    private async Task SeedStartupFocusAsync()
    {
        try
        {
            await PokeAccessibilityTreesAsync().ConfigureAwait(false);
            await TryBootstrapFocusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiEventClient] startup focus seed failed: {ex.Message}");
        }
    }

    // Wrapper that guarantees the in-flight-scan slot is cleared once the scan settles. The
    // leading Yield forces asynchronous completion so a synchronously-failing scan can't run
    // its finally BEFORE the caller's slot assignment lands — that would cache a settled task
    // forever and disable every future bootstrap on this connection.
    private async Task<AtSpiElementRef?> BootstrapFocusAsync(DBusConnection conn, CancellationTokenSource cts)
    {
        await Task.Yield();
        try
        {
            // A real focus event that landed while the walk ran is authoritative — prefer it
            // over the walk's own (possibly empty) result rather than reporting no focus.
            return await ScanForFocusedElementAsync(conn, cts.Token).ConfigureAwait(false)
                ?? CurrentFocusedElement;
        }
        catch (OperationCanceledException)
        {
            // The deadline tripped or StopAsync tore down the connection mid-scan; hand back any
            // event-sourced focus that arrived meanwhile, else the next dictation retries.
            Trace.WriteLine("[AtSpiEventClient] focus bootstrap cancelled before a FOCUSED element was found.");
            return CurrentFocusedElement;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiEventClient] focus bootstrap failed: {ex.Message}");
            return CurrentFocusedElement;
        }
        finally
        {
            lock (_focusLock)
            {
                // Surrender the slot only if it is still ours: a StopAsync/reset (or a newer
                // scan) may already have installed a different CTS, and clearing that would
                // disable the live scan's dedup.
                if (ReferenceEquals(_focusBootstrapCts, cts))
                {
                    _focusBootstrap = null;
                    _focusBootstrapCts = null;
                }
            }

            // Cancel before disposing: an early return (one app yielded focus while others were
            // still probing) leaves those per-app tasks running, and Dispose alone would drop the
            // deadline timer without signalling them — so they'd keep issuing timeout-bound D-Bus
            // probes detached. Cancelling first lets the token bound them too. No-op if the scan
            // already completed or StopAsync cancelled it.
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }
    }

    // Locates the element currently holding keyboard focus by walking the a11y tree: find
    // the application window carrying the ACTIVE state, then breadth-first-search its
    // subtree for FOCUSED nodes. Only used when no state-changed:focused signal has been
    // observed on this connection — AT-SPI has no "who has focus right now?" query. The walk
    // itself also materializes lazily-built trees (Qt/KF6 creates accessibles on demand), so
    // it doubles as the first-contact unlock for those apps. Collection.GetMatches could do
    // this server-side in one call, but toolkit support for Collection is too patchy to rely
    // on; the bounded walk works everywhere.
    private async Task<AtSpiElementRef?> ScanForFocusedElementAsync(
        DBusConnection conn,
        CancellationToken ct
    )
    {
        var apps = await GetChildrenAsync(conn, RegistryBusName, RegistryRootPath, ct)
            .ConfigureAwait(false);

        // Every app's active-window probe runs concurrently, and each result is walked the
        // moment that app finishes — NOT after a barrier — so one hung bridge that eats the whole
        // deadline can't keep the responsive app that actually holds the focus from being
        // searched. Per-app failures resolve to empty lists (FindActiveWindowsAsync never throws).
        var windowTasks = apps.Select(app => FindActiveWindowsAsync(conn, app, ct)).ToList();
        while (windowTasks.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var done = await Task.WhenAny(windowTasks).ConfigureAwait(false);
            windowTasks.Remove(done);

            foreach (var window in done.Result)
            {
                var focused = await FindFocusedElementsAsync(conn, window, ct)
                    .ConfigureAwait(false);
                if (focused.Count == 0)
                {
                    continue;
                }

                // SeedFocus re-checks cancellation while holding _focusLock — the same lock
                // StopAsync cancels under — so a reset racing this walk can't have its cleared
                // focus repopulated with an element from the torn-down connection.
                return SeedFocus(focused, ct);
            }
        }

        Trace.WriteLine("[AtSpiEventClient] focus bootstrap found no FOCUSED element.");
        return null;
    }

    // The top-level windows of one application that report the ACTIVE state (the window the
    // compositor currently has activated). Never throws; a failing app yields none.
    private static async Task<List<AtSpiElementRef>> FindActiveWindowsAsync(
        DBusConnection conn,
        AtSpiElementRef appRoot,
        CancellationToken ct
    )
    {
        var active = new List<AtSpiElementRef>();
        List<AtSpiElementRef> windows;
        try
        {
            windows = await GetChildrenAsync(conn, appRoot.BusName, appRoot.ObjectPath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[AtSpiEventClient] bootstrap window enumeration of {appRoot.BusName} failed: {ex.Message}"
            );
            return active;
        }

        foreach (var window in windows.Take(MaxBootstrapWindowsPerApp))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var states = await GetStateWord0Async(conn, window, ct).ConfigureAwait(false);
                if (HasState(states, StateActiveBit))
                {
                    active.Add(window);
                }
            }
            catch (Exception ex)
            {
                // One defunct window must not skip its siblings.
                Trace.WriteLine(
                    $"[AtSpiEventClient] bootstrap state read on {appRoot.BusName} failed: {ex.Message}"
                );
            }
        }

        return active;
    }

    // Bounded BFS below an active window collecting elements that carry FOCUSED. Collects up
    // to MaxBootstrapSeeds rather than stopping at the first hit: a shallow hit can be a
    // structural container with no readable text while the widget that matters sits deeper.
    private static async Task<List<AtSpiElementRef>> FindFocusedElementsAsync(
        DBusConnection conn,
        AtSpiElementRef window,
        CancellationToken ct
    )
    {
        var found = new List<AtSpiElementRef>();
        var queue = new Queue<(AtSpiElementRef Node, int Depth)>();
        queue.Enqueue((window, 0));
        var visited = 0;

        while (
            queue.Count > 0
            && visited < MaxBootstrapNodesPerWindow
            && found.Count < MaxBootstrapSeeds
            && !ct.IsCancellationRequested
        )
        {
            var (node, depth) = queue.Dequeue();
            visited++;
            try
            {
                var states = await GetStateWord0Async(conn, node, ct).ConfigureAwait(false);
                if (HasState(states, StateFocusedBit))
                {
                    found.Add(node);
                }

                // Stop descending once the seed cap is hit (or the depth limit): otherwise this
                // node's children fetch still runs before the while-condition re-checks, and a
                // hung leaf there could burn the deadline and make SeedFocus discard the
                // already-complete result as cancelled.
                if (found.Count >= MaxBootstrapSeeds || depth >= MaxBootstrapDepth)
                {
                    continue;
                }

                foreach (
                    var child in await GetChildrenAsync(conn, node.BusName, node.ObjectPath, ct)
                        .ConfigureAwait(false)
                )
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex)
            {
                // A defunct node aborts only its own subtree, not the walk.
                Trace.WriteLine($"[AtSpiEventClient] bootstrap walk step failed: {ex.Message}");
            }
        }

        return found;
    }

    // Installs the bootstrap result exactly as the focus signal handler would have — unless a
    // real event arrived mid-scan (the event is fresher; keep it). The deepest match becomes
    // current (BFS order is shallow-first, and the deepest FOCUSED node is the widget
    // itself); every match lands in the recent history so consumers' candidate fallback can
    // probe the one with readable text. FocusChanged is deliberately NOT raised: this
    // reconstructs state a missed past event should have left behind — focus didn't move now.
    private AtSpiElementRef SeedFocus(List<AtSpiElementRef> found, CancellationToken ct)
    {
        lock (_focusLock)
        {
            // Atomic with StopAsync's cancel+clear (both under _focusLock): if a reset already
            // won the lock, bail instead of seeding an element from the dead connection.
            ct.ThrowIfCancellationRequested();

            if (_currentFocused is { } raced)
            {
                return raced;
            }

            foreach (var element in found)
            {
                _recentFocused.Remove(element);
                _recentFocused.Insert(0, element);
            }

            while (_recentFocused.Count > RecentFocusCapacity)
            {
                _recentFocused.RemoveAt(RecentFocusCapacity);
            }

            _currentFocused = found[^1];
            return found[^1];
        }
    }

    private static async Task<uint> GetStateWord0Async(
        DBusConnection conn,
        AtSpiElementRef element,
        CancellationToken ct = default
    )
    {
        MessageBuffer message;
        using (var writer = conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: element.BusName,
                path: element.ObjectPath,
                @interface: AccessibleInterface,
                member: "GetState"
            );
            message = writer.CreateMessage();
        }

        return await conn.CallMethodAsync(message, s_readStateSetWord0)
            .WaitAsync(s_callTimeout, ct)
            .ConfigureAwait(false);
    }

    private static bool HasState(uint stateWord0, int bit)
    {
        return (stateWord0 & (1u << bit)) != 0;
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

        // ReSharper disable once LoopCanBeConvertedToQuery -- the body awaits a D-Bus call per
        // child and OR-accumulates; that isn't a pure query and can't be a LINQ expression.
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

                await conn.CallMethodAsync(message).WaitAsync(s_callTimeout).ConfigureAwait(false);
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
        string objectPath,
        CancellationToken ct = default
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

        return await conn.CallMethodAsync(message, s_readElementRefArray)
            .WaitAsync(s_callTimeout, ct)
            .ConfigureAwait(false);
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

            // Our RegisterEvent state lives inside registryd, and registryd can be replaced
            // mid-session (the a11y bus broker force-disconnects it under event-flood quota
            // pressure and D-Bus activation spawns a fresh instance with an empty listener
            // table — observed live). Watch the registry name's owner and re-register with
            // every new incarnation, exactly like GTK's own bridge does.
            _registryOwnerSubscription = await conn.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = "org.freedesktop.DBus",
                    Interface = "org.freedesktop.DBus",
                    Member = "NameOwnerChanged",
                    Arg0 = RegistryBusName
                },
                s_readNameOwnerChanged,
                HandleRegistryOwnerChanged,
                ObserverFlags.None,
                emitOnCapturedContext: false
            ).ConfigureAwait(false);

            // Tell registryd which events have listeners so toolkits that gate event emission on
            // demand (GTK) actually broadcast them. focused is permanent (focus tracking must run
            // before any dictation arms). text-changed is registered ONLY when a lease is live: a
            // standing text-changed registration makes every GTK app emit an event per text
            // mutation (terminals: one per output line), which floods the a11y bus — so it is
            // gated on the refcount, which survives reconnects and reflects live holders here.
            await RegisterEventAsync(conn, FocusedEventName).ConfigureAwait(false);

            // Fresh connection: registryd holds none of our registrations. Drive text-changed from
            // the live lease count through the same serialized reconciler the leases use, so a
            // concurrent acquire/release can't race this initial registration.
            lock (_textEventLock)
            {
                _textChangedRegistered = false;
            }

            await ReconcileTextChangedAsync().ConfigureAwait(false);

            // Fresh connection: re-arm the one-reset-per-connection guard so a later
            // disconnect of THIS connection schedules its own reconnect.
            Interlocked.Exchange(ref _resetScheduled, 0);

            // The listener only observes focus changes from here on — AT-SPI replays
            // nothing — so the field the user is ALREADY in would stay unknown until they
            // refocus something. Seed it in the background; connecting must stay fast, and
            // a cold-start arm additionally awaits its own bootstrap if this hasn't landed.
            _ = SeedStartupFocusAsync();

            return true;
        }
        catch (Exception ex)
        {
            LogOnce($"AT-SPI event client start failed: {ex.Message}");
            // A connection may have been made before AddMatchAsync/RegisterEventAsync threw.
            // Tear down any partial state so we don't leak a live connection/match, and so the
            // next EnsureStartedAsync retries from a clean slate.
            TearDownConnection();
            return false;
        }
    }

    private void HandleStateChanged(Exception? exception, AtSpiSignal signal, object? readerState, object? handlerState)
    {
        // Only successful reads carry a signal. On error/disconnect the observer is invoked
        // with a non-null exception and a default value; schedule a reconnect rather than
        // acting on an empty AtSpiSignal.
        if (exception is not null)
        {
            OnObserverError(exception);
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
            OnObserverError(exception);
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

    private void HandleRegistryOwnerChanged(
        Exception? exception,
        string newOwner,
        object? readerState,
        object? handlerState
    )
    {
        if (exception is not null)
        {
            OnObserverError(exception);
            return;
        }

        if (string.IsNullOrEmpty(newOwner))
        {
            // The name is momentarily unowned (old registryd gone); the replacement's
            // takeover fires this signal again with a real owner.
            return;
        }

        var conn = _connection;
        if (conn is null)
        {
            return;
        }

        Trace.WriteLine(
            "[AtSpiEventClient] a11y registry restarted; re-registering event listeners."
        );
        _ = ReRegisterEventsAsync(conn);
    }

    // Instance (not static) so it can read the live lease count: a restarted registryd has an
    // empty listener table, so focused must ALWAYS be re-registered, but text-changed only when a
    // lease is currently held — re-registering it unconditionally would reinstate the flood.
    private async Task ReRegisterEventsAsync(DBusConnection conn)
    {
        try
        {
            await RegisterEventAsync(conn, FocusedEventName).ConfigureAwait(false);

            // The restarted registryd came up with an empty listener table, so it has forgotten our
            // text-changed registration regardless of what we last drove. Clear the flag and
            // reconcile so a live lease re-registers (and no lease stays a stale no-op).
            lock (_textEventLock)
            {
                _textChangedRegistered = false;
            }

            await ReconcileTextChangedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[AtSpiEventClient] re-register after registry restart failed: {ex.Message}"
            );
        }
    }

    // An observer exception is per-connection-fatal (bus daemon restart, socket closed).
    // Without a reset, _started stays true and EnsureStartedAsync returns the dead
    // connection forever — an a11y-bus restart would permanently kill the feature. Reset
    // once; the next EnsureStartedAsync (next dictation arm) reconnects and re-registers.
    private void OnObserverError(Exception exception)
    {
        if (_disposed || Interlocked.Exchange(ref _resetScheduled, 1) == 1)
        {
            return;
        }

        Trace.WriteLine(
            $"[AtSpiEventClient] a11y bus connection lost ({exception.Message}); scheduling reconnect."
        );
        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // Resetting a dead connection is best-effort and must not throw.
            }
        });
    }

    // ReSharper disable once InconsistentNaming -- "a11y" is the standard accessibility numeronym mirroring org.a11y.Bus; ReSharper's PascalCase splitter mis-reads "11y".
    private async Task<string?> ResolveA11yBusAddressAsync()
    {
        // Standard override honored by libatspi and Qt; takes precedence over the
        // org.a11y.Bus lookup (test rigs, nested/remote sessions).
        var overrideAddress = Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS");
        if (!string.IsNullOrWhiteSpace(overrideAddress))
        {
            return overrideAddress;
        }

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

            return await session.CallMethodAsync(message, s_readString)
                .WaitAsync(s_callTimeout)
                .ConfigureAwait(false);
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

        await conn.CallMethodAsync(message).WaitAsync(s_callTimeout).ConfigureAwait(false);
    }

    // A single "s" arg (event only) mirrors what we registered with. NOTE: this reply is not proof
    // of removal — AT-SPI Registry v2 (at-spi2-core 2.60.4) ACKs it without removing the listener,
    // so callers verify via IsTextChangedRegisteredAsync rather than trusting the reply.
    private static async Task DeregisterEventAsync(DBusConnection conn, string eventName)
    {
        MessageBuffer message;
        using (var writer = conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: RegistryBusName,
                path: RegistryPath,
                @interface: RegistryInterface,
                member: "DeregisterEvent",
                signature: "s"
            );
            writer.WriteString(eventName);
            message = writer.CreateMessage();
        }

        await conn.CallMethodAsync(message).WaitAsync(s_callTimeout).ConfigureAwait(false);
    }

    // True when the registry still lists a text-changed registration for THIS connection. Lets the
    // reconciler detect a registryd that ACKs DeregisterEvent without removing it (AT-SPI Registry
    // v2) and stop re-registering, which would otherwise stack a duplicate on every lease cycle.
    private static async Task<bool> IsTextChangedRegisteredAsync(DBusConnection conn)
    {
        var me = conn.UniqueName;
        if (string.IsNullOrEmpty(me))
        {
            return false;
        }

        var events = await GetRegisteredEventsAsync(conn).ConfigureAwait(false);
        return events.Any(e =>
            string.Equals(e.Sender, me, StringComparison.Ordinal)
            && e.EventType.Contains("TextChanged", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<(string Sender, string EventType)>> GetRegisteredEventsAsync(
        DBusConnection conn
    )
    {
        MessageBuffer message;
        using (var writer = conn.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: RegistryBusName,
                path: RegistryPath,
                @interface: RegistryInterface,
                member: "GetRegisteredEvents"
            );
            message = writer.CreateMessage();
        }

        return await conn.CallMethodAsync(message, s_readRegisteredEvents)
            .WaitAsync(s_callTimeout)
            .ConfigureAwait(false);
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

        return await conn.CallMethodAsync(message, s_readVariantInt32)
            .WaitAsync(s_callTimeout)
            .ConfigureAwait(false);
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

        return await conn.CallMethodAsync(message, s_readString)
            .WaitAsync(s_callTimeout)
            .ConfigureAwait(false);
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

    // at-spi2-core 2.52 (Ubuntu/Mint) answers a property Get for an interface the element does not
    // implement with the *generic* error name and the literal text "Get failed", where 2.60
    // (Fedora) answers InvalidArgs. Same meaning — "no Text interface here" — so match the name and
    // the text together. Never match the bare name: org.freedesktop.DBus.Error.Failed is D-Bus's
    // catch-all, and suppressing all of it would hide real faults.
    private const string GenericErrorName = "org.freedesktop.DBus.Error.Failed";
    private const string PropertyGetFailureText = "Get failed";

    // AT-SPI text reads run against whatever third-party app holds focus, so failure is expected,
    // not exceptional: terminals / TUIs / Claude Code don't implement org.a11y.atspi.Text, and an
    // accessible can vanish between the focus signal and the read. Tmds surfaces these as
    // DBusErrorReplyException; the well-known "not readable / gone / unresponsive" names are benign.
    private static bool IsExpectedUnreadableTarget(Exception ex)
    {
        // A call timeout (WaitAsync) means the target app is hung or too busy to answer —
        // "can't learn from this app right now", not a TypeWhisper fault.
        if (ex is TimeoutException)
        {
            return true;
        }

        return ex is DBusErrorReplyException && IsBenignReadErrorMessage(ex.Message);
    }

    // Split out as a pure string predicate so the classification is unit-testable: Tmds exposes no
    // public way to construct a DBusErrorReplyException with a chosen error name.
    internal static bool IsBenignReadErrorMessage(string message)
    {
        if (
            Array.Exists(
                s_benignReadErrorNames,
                name => message.StartsWith(name, StringComparison.Ordinal)
            )
        )
        {
            return true;
        }

        return message.StartsWith(GenericErrorName, StringComparison.Ordinal)
            && message.Contains(PropertyGetFailureText, StringComparison.Ordinal);
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

    // Handle returned by AcquireTextChangedEvents. Idempotent: only the first Dispose releases the
    // underlying lease, so a caller (or a double-dispose from finalization patterns) can't drive
    // the refcount negative or deregister twice. Interlocked keeps that guarantee thread-safe.
    private sealed class TextChangedLease(AtSpiEventClient owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseTextChangedEvents();
            }
        }
    }
}
