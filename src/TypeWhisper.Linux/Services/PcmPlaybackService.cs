using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services;

/// <summary>Streams plugin-provided PCM to the first supported executable on PATH.</summary>
public sealed class PcmPlaybackService : IPluginPcmPlaybackService
{
    // pw-play gained --raw in PipeWire 1.4; before that the flag is rejected outright and
    // "-" already implies raw. Neither argument vector works on both, so the flag is probed
    // once per resolved binary.
    private static readonly Dictionary<string, bool> s_rawFlagSupport = [];
    private static readonly Lock s_rawFlagGate = new();

    private readonly IPluginProcessSupervisor _processes;
    private readonly Func<ResolvedPcmPlayer?> _resolvePlayer;
    private readonly Func<ResolvedPcmPlayer, bool> _supportsRawFlag;

    public PcmPlaybackService(PluginProcessSupervisorScope processes)
        : this(processes, PcmPlayerResolver.Resolve)
    {
    }

    internal PcmPlaybackService(
        IPluginProcessSupervisor processes,
        Func<ResolvedPcmPlayer?> resolvePlayer,
        Func<ResolvedPcmPlayer, bool>? supportsRawFlag = null
    )
    {
        _processes = processes;
        _resolvePlayer = resolvePlayer;
        _supportsRawFlag = supportsRawFlag ?? ProbeRawFlagSupport;
    }

    public bool IsAvailable => _resolvePlayer() is not null;

    public Task<ITtsPlaybackSession> PlayAsync(
        PcmPlaybackRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Preparation is intentionally synchronous. The session owns these bytes before this
        // method returns, so a provider may immediately reuse or mutate its response buffer.
        var pcm16 = PreparePcm16(request);
        var player = _resolvePlayer();
        if (player is null)
        {
            return Task.FromResult<ITtsPlaybackSession>(InactivePlaybackSession.Instance);
        }

        var arguments = BuildArguments(
            player.Kind,
            request.SampleRate,
            request.Channels,
            player.Kind == PcmPlayerKind.PwPlay && _supportsRawFlag(player)
        );
        var started = _processes.StartSession(
            new ProcessCommand(player.AbsolutePath, arguments),
            new ProcessSessionOptions(
                RedirectStandardInput: true,
                StandardError: ProcessSessionOutputMode.Lines
            )
        );
        if (started.Session is not { } processSession)
        {
            Trace.TraceWarning(
                $"[PcmPlayback] {player.Kind} ({player.AbsolutePath}) failed to start: "
                + Bound(started.StartError)
            );
            return Task.FromResult<ITtsPlaybackSession>(InactivePlaybackSession.Instance);
        }

        Trace.WriteLine(
            $"[PcmPlayback] Started {player.Kind} ({player.AbsolutePath}) for "
            + $"{request.SampleRate.ToString(CultureInfo.InvariantCulture)} Hz, "
            + $"{request.Channels.ToString(CultureInfo.InvariantCulture)} channel(s)."
        );
        return Task.FromResult<ITtsPlaybackSession>(
            new PcmPlaybackSession(processSession, pcm16, player, cancellationToken)
        );
    }

    private static byte[] PreparePcm16(PcmPlaybackRequest request)
    {
        if (request.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.SampleRate,
                "PCM sample rate must be positive."
            );
        }

        if (request.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Channels,
                "PCM channel count must be positive."
            );
        }

        var bytesPerSample = request.Format switch
        {
            PcmSampleFormat.Signed16LittleEndian => sizeof(short),
            PcmSampleFormat.Float32 => sizeof(float),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Format,
                "Unsupported PCM sample format."
            ),
        };

        int frameSize;
        try
        {
            frameSize = checked(bytesPerSample * request.Channels);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Channels,
                "PCM channel count is too large."
            );
        }

        if (request.Payload.IsEmpty)
        {
            throw new ArgumentException("PCM payload must not be empty.", nameof(request));
        }

        if (request.Payload.Length % frameSize != 0)
        {
            throw new ArgumentException(
                "PCM payload length must contain complete interleaved frames.",
                nameof(request)
            );
        }

        return request.Format == PcmSampleFormat.Signed16LittleEndian
            ? request.Payload.ToArray()
            : ConvertFloat32ToPcm16LittleEndian(request.Payload.Span);
    }

    internal static byte[] ConvertFloat32ToPcm16LittleEndian(
        ReadOnlySpan<byte> float32LittleEndian
    )
    {
        if (float32LittleEndian.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                "Float32 payload length must be a multiple of four bytes.",
                nameof(float32LittleEndian)
            );
        }

        var result = new byte[float32LittleEndian.Length / sizeof(float) * sizeof(short)];
        for (var sourceOffset = 0; sourceOffset < float32LittleEndian.Length; sourceOffset += 4)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(
                float32LittleEndian.Slice(sourceOffset, sizeof(float))
            );
            var value = BitConverter.Int32BitsToSingle(bits);

            // Keep this algorithm bit-for-bit aligned with the original Supertonic adapter.
            var clamped = Math.Max(-1.0f, Math.Min(1.0f, value));
            var pcm16 = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                result.AsSpan(sourceOffset / 2, sizeof(short)),
                pcm16
            );
        }

        return result;
    }

    private static string[] BuildArguments(
        PcmPlayerKind kind,
        int sampleRate,
        int channels,
        bool pwPlaySupportsRawFlag
    )
    {
        var rate = sampleRate.ToString(CultureInfo.InvariantCulture);
        var channelCount = channels.ToString(CultureInfo.InvariantCulture);
        return kind switch
        {
            // Omitting --raw is fine here: pre-1.4 pw-play treats "-" as raw already.
            PcmPlayerKind.PwPlay => pwPlaySupportsRawFlag
                ? new[]
                {
                    "--raw",
                    $"--rate={rate}",
                    $"--channels={channelCount}",
                    "--format=s16",
                    "-",
                }
                : new[]
                {
                    $"--rate={rate}",
                    $"--channels={channelCount}",
                    "--format=s16",
                    "-",
                },
            PcmPlayerKind.Paplay =>
            [
                "--raw",
                $"--rate={rate}",
                $"--channels={channelCount}",
                "--format=s16le",
            ],
            PcmPlayerKind.Aplay =>
            [
                "--file-type=raw",
                "--format=S16_LE",
                $"--rate={rate}",
                $"--channels={channelCount}",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>
    ///     Asks the resolved pw-play whether it accepts --raw, caching per binary. An
    ///     unreadable usage screen falls back to the pre-1.4 vector, which is what the
    ///     long-lived distribution releases ship.
    /// </summary>
    private bool ProbeRawFlagSupport(ResolvedPcmPlayer player)
    {
        lock (s_rawFlagGate)
        {
            if (s_rawFlagSupport.TryGetValue(player.AbsolutePath, out var cached))
            {
                return cached;
            }
        }

        var supported = false;
        try
        {
            var probe = _processes.RunProbe(
                new ProcessCommand(player.AbsolutePath, ["--help"]),
                new ProcessOneShotOptions(Timeout: TimeSpan.FromSeconds(2))
            );
            // Usage goes to stdout on success and stderr on older builds, so scan both.
            supported =
                probe.StandardOutputText.Contains("--raw", StringComparison.Ordinal)
                || probe.StandardErrorText.Contains("--raw", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"[PcmPlayback] {player.Kind} --raw probe failed: {Bound(ex.Message)}"
            );
        }

        lock (s_rawFlagGate)
        {
            s_rawFlagSupport[player.AbsolutePath] = supported;
        }

        return supported;
    }

    private static string Bound(string? value)
    {
        const int limit = 2_048;
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<none>";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + "…";
    }

    private sealed class PcmPlaybackSession : ITtsPlaybackSession
    {
        private const int StderrLimit = 2_048;

        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly CancellationTokenSource _feederCancellation = new();
        private readonly Task _feeder;
        private readonly ResolvedPcmPlayer _player;
        private readonly IPluginProcessSession _session;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stderrCapture;
        private int _completed;
        private int _stopRequested;

        public PcmPlaybackSession(
            IPluginProcessSession session,
            byte[] pcm16,
            ResolvedPcmPlayer player,
            CancellationToken cancellationToken
        )
        {
            _session = session;
            _player = player;
            _stderrCapture = CaptureStderrAsync();
            // CancellationToken.None is deliberate: the caller's token is honoured through the
            // registration below, which cancels _feederCancellation. Handing it to Task.Run instead
            // would leave _feeder pre-cancelled and fault the await in ObserveCompletionAsync.
            _feeder = Task.Run(() => FeedAsync(pcm16), CancellationToken.None);
            _cancellationRegistration = cancellationToken.Register(
                static state => ((PcmPlaybackSession)state!).Stop(),
                this
            );
            _ = ObserveCompletionAsync();
        }

        public bool IsActive =>
            Volatile.Read(ref _completed) == 0 && _session.IsRunning;

        public event EventHandler? Completed;

        public void Stop()
        {
            if (
                Volatile.Read(ref _completed) != 0
                || Interlocked.Exchange(ref _stopRequested, 1) != 0
            )
            {
                return;
            }

            try
            {
                _feederCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released the feeder.
            }

            try
            {
                _session.Terminate();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"[PcmPlayback] {_player.Kind} stop failed: {Bound(ex.Message)}"
                );
            }
        }

        private async Task FeedAsync(byte[] pcm16)
        {
            try
            {
                await _session.WriteStandardInputAsync(pcm16, _feederCancellation.Token)
                    .ConfigureAwait(false);
                await _session.CompleteStandardInputAsync(_feederCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_feederCancellation.IsCancellationRequested)
            {
                // Stop/caller cancellation/process completion owns termination.
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"[PcmPlayback] {_player.Kind} stdin feed failed: {Bound(ex.Message)}"
                );
                Stop();
            }
        }

        private async Task CaptureStderrAsync()
        {
            try
            {
                await foreach (var line in _session.ReadOutputAsync().ConfigureAwait(false))
                {
                    if (line.Stream != ProcessStream.StandardError || _stderr.Length >= StderrLimit)
                    {
                        continue;
                    }

                    if (_stderr.Length > 0)
                    {
                        _stderr.Append(' ');
                    }

                    var remaining = StderrLimit - _stderr.Length;
                    _stderr.Append(line.Text.AsSpan(0, Math.Min(remaining, line.Text.Length)));
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"[PcmPlayback] {_player.Kind} stderr capture failed: {Bound(ex.Message)}"
                );
            }
        }

        private async Task ObserveCompletionAsync()
        {
            ProcessExitOutcome? outcome = null;
            string? completionError = null;
            try
            {
                outcome = await _session.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                completionError = ex.Message;
            }
            finally
            {
                await _feederCancellation.CancelAsync().ConfigureAwait(false);
            }

            await _feeder.ConfigureAwait(false);
            await _stderrCapture.ConfigureAwait(false);

            var classification = outcome switch
            {
                { Reason: ProcessExitReason.Exited, ExitCode: 0 } => "natural completion",
                { Reason: ProcessExitReason.Exited } => "nonzero exit",
                { Reason: ProcessExitReason.Terminated } => "terminated",
                _ => "completion failure",
            };
            Trace.WriteLine(
                $"[PcmPlayback] {_player.Kind} ({_player.AbsolutePath}) {classification}; "
                + $"reason={outcome?.Reason.ToString() ?? "unknown"}; "
                + $"exit={outcome?.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}; "
                + $"stderr={Bound(_stderr.Length > 0 ? _stderr.ToString() : completionError)}"
            );
            Finish();
        }

        private void Finish()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
            _feederCancellation.Dispose();
            try
            {
                Completed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"[PcmPlayback] {_player.Kind} completion subscriber failed: {Bound(ex.Message)}"
                );
            }
        }
    }

    private sealed class InactivePlaybackSession : ITtsPlaybackSession
    {
        public static InactivePlaybackSession Instance { get; } = new();

        public bool IsActive => false;

        public event EventHandler? Completed
        {
            add => value?.Invoke(this, EventArgs.Empty);
            remove { }
        }

        public void Stop()
        {
        }
    }
}
