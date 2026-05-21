using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Reads <see cref="InputEvent" /> records from a single
///     <c>/dev/input/eventN</c> device. Opens read-only with
///     <see cref="FileShare.ReadWrite" /> so the kernel keeps delivering to
///     every other reader on the same node.
/// </summary>
internal sealed class EvdevDeviceReader : IAsyncDisposable
{
    public delegate void KeyEventHandler(string devicePath, int linuxKeyCode, bool pressed);

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string, Exception> _onFailure;
    private readonly KeyEventHandler _onKeyEvent;

    private int _disposed;
    private Task? _readLoop;
    private FileStream? _stream;

    public EvdevDeviceReader(
        string path,
        KeyEventHandler onKeyEvent,
        Action<string, Exception> onFailure
    )
    {
        Path = path;
        _onKeyEvent = onKeyEvent;
        _onFailure = onFailure;
    }

    public string Path { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
            /* already disposed */
        }

        try
        {
            _stream?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevReader] Dispose stream threw: {ex.Message}");
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch
            {
                /* timeout or cancellation — best effort */
            }
        }

        _cts.Dispose();
    }

    public bool TryStart()
    {
        try
        {
            _stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                0,
                true
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevReader] Open {Path} failed: {ex.Message}");
            return false;
        }

        _readLoop = Task.Run(() => RunAsync(_cts.Token));
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        var buf = new byte[InputEvent.SizeBytes];
        Exception? terminating = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = 0;
                while (read < InputEvent.SizeBytes)
                {
                    int n;
                    try
                    {
                        n = await stream
                            .ReadAsync(buf.AsMemory(read, InputEvent.SizeBytes - read), ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException ex)
                    {
                        // Device disappeared (unplug) or kernel closed the
                        // node. Report so the backend can drop us from its
                        // reader map and clear any stuck per-device state.
                        terminating = ex;
                        return;
                    }

                    if (n == 0)
                    {
                        terminating = new EndOfStreamException(
                            $"{Path} reached EOF (device removed?)"
                        );
                        return;
                    }

                    read += n;
                }

                var evt = MemoryMarshal.Read<InputEvent>(buf);
                if (evt.Type != InputEvent.EV_KEY)
                {
                    continue;
                }

                if (evt.Value == InputEvent.Repeated)
                {
                    continue;
                }

                _onKeyEvent(Path, evt.Code, evt.Value == InputEvent.Pressed);
            }
        }
        catch (Exception ex)
        {
            terminating = ex;
        }
        finally
        {
            if (terminating is not null && Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _onFailure(Path, terminating);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[EvdevReader] onFailure callback threw: {ex.Message}");
                }
            }
        }
    }
}