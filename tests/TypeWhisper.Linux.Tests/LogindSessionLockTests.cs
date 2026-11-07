using System.Diagnostics;
using Tmds.DBus.Protocol;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Drives <see cref="LogindSessionActivityMonitor" /> with real D-Bus signals from a fake
///     logind on a private dbus-daemon.
/// </summary>
public sealed class LogindSessionLockTests : IAsyncLifetime
{
    private static readonly TimeSpan s_confirmationDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_waitLimit = TimeSpan.FromSeconds(10);

    private PrivateBus _bus = null!;
    private FakeLogind _logind = null!;
    private LogindSessionActivityMonitor _monitor = null!;

    public async Task InitializeAsync()
    {
        _bus = PrivateBus.Start();
        _logind = await FakeLogind.StartAsync(_bus.Address);
        _monitor = new LogindSessionActivityMonitor(() => _bus.Address, s_confirmationDelay);
        await _monitor.InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _monitor.DisposeAsync();
        _logind.Dispose();
        _bus.Dispose();
    }

    [DBusDaemonFact]
    public async Task Lock_that_logind_never_confirms_enables_input_again()
    {
        Assert.True(_monitor.IsInputAllowed);

        // hyprlock: Lock is requested, but LockedHint stays false and no Unlock follows.
        _logind.EmitLock();

        await WaitUntilAsync(() => !_monitor.IsInputAllowed);
        await WaitUntilAsync(() => _monitor.IsInputAllowed);
    }

    [DBusDaemonFact]
    public async Task Lock_confirmed_by_LockedHint_keeps_input_blocked_until_unlock()
    {
        var readsBeforeLock = _logind.GetAllCount;

        // A locker that reports its state sets LockedHint alongside the Lock request.
        _logind.LockedHint = true;
        _logind.EmitLock();
        _logind.EmitLockedHintChanged();

        await WaitUntilAsync(() => _logind.GetAllCount > readsBeforeLock);
        Assert.False(_monitor.IsInputAllowed);

        _logind.LockedHint = false;
        _logind.EmitLockedHintChanged();

        await WaitUntilAsync(() => _monitor.IsInputAllowed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > s_waitLimit)
            {
                throw new TimeoutException($"Condition not met within {s_waitLimit}.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class DBusDaemonFactAttribute : FactAttribute
    {
        public DBusDaemonFactAttribute()
        {
            if (PrivateBus.FindDaemon() is null)
            {
                Skip = "dbus-daemon is not installed.";
            }
        }
    }

    private sealed class PrivateBus : IDisposable
    {
        private readonly Process _daemon;

        private PrivateBus(Process daemon, string address)
        {
            _daemon = daemon;
            Address = address;
        }

        public string Address { get; }

        public static string? FindDaemon()
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Join(directory, "dbus-daemon"))
                .FirstOrDefault(File.Exists);
        }

        public static PrivateBus Start()
        {
            var startInfo = new ProcessStartInfo(FindDaemon() ?? "dbus-daemon")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "--session", "--nofork", "--print-address=1", "--address=unix:tmpdir=/tmp" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            var daemon = Process.Start(startInfo)
                ?? throw new InvalidOperationException("dbus-daemon did not start.");
            var address = daemon.StandardOutput.ReadLine();
            // ReSharper disable once InvertIf -- the failure path stays a guard clause ahead of the normal return
            if (string.IsNullOrWhiteSpace(address))
            {
                daemon.Kill();
                throw new InvalidOperationException("dbus-daemon printed no address.");
            }

            return new PrivateBus(daemon, address.Trim());
        }

        public void Dispose()
        {
            try
            {
                _daemon.Kill();
                _daemon.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            _daemon.Dispose();
        }
    }

    private sealed class FakeLogind : IPathMethodHandler, IDisposable
    {
        private const string SessionPath = "/org/freedesktop/login1/session/test";
        private const string SessionInterface = "org.freedesktop.login1.Session";

        private readonly DBusConnection _connection;
        private int _getAllCount;

        private FakeLogind(DBusConnection connection)
        {
            _connection = connection;
        }

        public volatile bool LockedHint;

        public int GetAllCount => Volatile.Read(ref _getAllCount);

        public string Path => "/org/freedesktop/login1";

        public bool HandlesChildPaths => true;

        public static async Task<FakeLogind> StartAsync(string address)
        {
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            var logind = new FakeLogind(connection);
            connection.AddMethodHandler(logind);
            await connection.RequestNameAsync("org.freedesktop.login1");
            return logind;
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            switch (request.InterfaceAsString, request.MemberAsString)
            {
                case ("org.freedesktop.login1.Manager", "GetSession" or "GetSessionByPID"):
                {
                    using var writer = context.CreateReplyWriter("o");
                    writer.WriteObjectPath(SessionPath);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case ("org.freedesktop.DBus.Properties", "GetAll")
                    when request.PathAsString == SessionPath:
                {
                    using var writer = context.CreateReplyWriter("a{sv}");
                    var properties = writer.WriteDictionaryStart();
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("Active");
                    writer.WriteVariantBool(true);
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("LockedHint");
                    writer.WriteVariantBool(LockedHint);
                    writer.WriteDictionaryEnd(properties);
                    Interlocked.Increment(ref _getAllCount);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }

            return ValueTask.CompletedTask;
        }

        public void EmitLock()
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, SessionPath, SessionInterface, "Lock");
            _connection.TrySendMessage(writer.CreateMessage());
        }

        public void EmitLockedHintChanged()
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(
                null,
                SessionPath,
                "org.freedesktop.DBus.Properties",
                "PropertiesChanged",
                "sa{sv}as"
            );
            writer.WriteString(SessionInterface);
            var changed = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("LockedHint");
            writer.WriteVariantBool(LockedHint);
            writer.WriteDictionaryEnd(changed);
            var invalidated = writer.WriteArrayStart(DBusType.String);
            writer.WriteArrayEnd(invalidated);
            _connection.TrySendMessage(writer.CreateMessage());
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}
