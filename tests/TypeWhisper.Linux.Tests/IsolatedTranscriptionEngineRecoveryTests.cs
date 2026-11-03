using TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.TranscriptionWorkerProbe;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     The recovery policy (retry, CPU fallback, pause) against scripted workers, so each case is
///     exact and fast. Real crashes are covered in <see cref="TranscriptionWorkerTests" />.
/// </summary>
public sealed class IsolatedTranscriptionEngineRecoveryTests
{
    private static readonly byte[] s_audio = [1, 2, 3, 4];

    private readonly WorkerProbePlugin _inner = new();
    private readonly Clock _clock = new();
    private readonly List<ScriptedWorker> _started = [];
    private readonly Queue<Func<ScriptedWorker, TranscriptionWorkerMessage>> _script = new();
    private int _statusChanges;

    public IsolatedTranscriptionEngineRecoveryTests()
    {
        _inner.SelectModel("small");
    }

    private IsolatedTranscriptionEngine Engine()
    {
        return new IsolatedTranscriptionEngine(
            _inner,
            (acceleration, _, _) =>
            {
                var worker = new ScriptedWorker(acceleration, _script);
                _started.Add(worker);
                return Task.FromResult<ITranscriptionWorkerConnection>(worker);
            },
            (_, _) => { },
            () => Interlocked.Increment(ref _statusChanges),
            _clock
        );
    }

    private void Then(Func<ScriptedWorker, TranscriptionWorkerMessage> step, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _script.Enqueue(step);
        }
    }

    private static TranscriptionWorkerMessage Crash(ScriptedWorker _)
    {
        throw new TranscriptionWorkerCrashedException("The local transcription engine stopped unexpectedly.", 139, "");
    }

    private static TranscriptionWorkerMessage Answer(ScriptedWorker worker)
    {
        return new TranscriptionWorkerMessage
        {
            Type = TranscriptionWorkerMessageTypes.Response,
            Result = new TranscriptionWorkerResult(worker.Acceleration.ToString(), null, 1, null, [], []),
            State = worker.State,
        };
    }

    private static TranscriptionWorkerMessage RestartRequired(ScriptedWorker worker)
    {
        return new TranscriptionWorkerMessage
        {
            Type = TranscriptionWorkerMessageTypes.Response,
            Error = new TranscriptionWorkerError("invalid-operation", "Restart TypeWhisper to try again."),
            State = worker.State with
            {
                Status = new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Cpu, "Using CPU", RequiresRestart: true),
            },
        };
    }

    // Loaded on the CPU after the GPU runtime failed; the worker reports that with every answer.
    private static TranscriptionWorkerMessage PinnedToTheCpu(ScriptedWorker worker)
    {
        return Answer(worker) with
        {
            State = worker.State with
            {
                Status = new TranscriptionAccelerationStatus(
                    TranscriptionAccelerationBackend.Cpu, "Using CPU", "CUDA unavailable", RequiresRestart: true),
            },
        };
    }

    [Fact]
    public async Task APauseEndsAfterItsCooldown()
    {
        var engine = Engine();
        Then(Crash, IsolatedTranscriptionEngine.CrashesBeforePause);
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        var started = _started.Count;

        _clock.Advance(IsolatedTranscriptionEngine.PauseAfterRepeatedCrashes - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        Assert.Equal(started, _started.Count);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Then(Answer);
        Assert.Equal("Auto", (await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None)).Text);
        Assert.Equal(1, _statusChanges);
    }

    [Fact]
    public async Task ASuccessResetsTheCrashCount()
    {
        var engine = Engine();
        Then(Crash, 2);
        Then(Answer);
        Then(Crash, 2);
        Then(Answer);

        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);

        Assert.Equal(0, _statusChanges);
    }

    [Fact]
    public async Task AHangIsNotRetried()
    {
        var engine = Engine();
        Then(_ => throw new TranscriptionWorkerUnresponsiveException("The local transcription engine stopped answering and was ended.", ""));

        await Assert.ThrowsAsync<TranscriptionWorkerUnresponsiveException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.Single(_started);
    }

    [Fact]
    public async Task AnExceededDeadlineIsNotRetriedOrCountedAsACrash()
    {
        var engine = Engine();
        Then(_ => throw new TranscriptionWorkerRequestTimeoutException("The local transcription engine took too long."), 3);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TranscriptionWorkerRequestTimeoutException>(() =>
                engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        }

        Assert.Equal(3, _started.Count);
        Assert.Equal(0, _statusChanges);
    }

    [Fact]
    public async Task ACanceledRequestIsNotRetried()
    {
        var engine = Engine();
        using var cancellation = new CancellationTokenSource();
        Then(_ =>
        {
            // ReSharper disable AccessToDisposedClosure -- the step runs inside the awaited request, before the using scope ends.
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
            // ReSharper restore AccessToDisposedClosure
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, cancellation.Token));

        Assert.Single(_started);
    }

    [Fact]
    public async Task AnEngineThatAsksForARestartGetsOneNewWorker()
    {
        var engine = Engine();
        Then(RestartRequired);
        Then(Answer);
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal(2, _started.Count);

        Then(RestartRequired, 2);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));
        Assert.Equal("Restart TypeWhisper to try again.", error.Message);
        Assert.Equal(3, _started.Count);
        // A new worker is the restart, so the status never asks for one.
        Assert.False(engine.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public async Task AnExplicitLoadReplacesAWorkerPinnedToTheCpuAfterItsGpuRuntimeFailed()
    {
        var engine = Engine();
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Then(PinnedToTheCpu, 2);
        Then(Answer);
        await engine.LoadModelAsync("small", CancellationToken.None);

        // Requests keep the working CPU worker; only an explicit load starts over.
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Single(_started);
        await engine.LoadModelAsync("small", CancellationToken.None);

        Assert.Equal(2, _started.Count);
        Assert.False(_started[0].IsAlive);
        Assert.Equal(TranscriptionAccelerationBackend.NvidiaCuda, engine.AccelerationStatus.ActiveBackend);
    }

    [Fact]
    public async Task AnEngineWithoutAGraphicsCardBackendIsPausedRatherThanSwitched()
    {
        _inner.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        var engine = Engine();
        Then(Crash, IsolatedTranscriptionEngine.CrashesBeforePause);

        var paused = await Assert.ThrowsAsync<TranscriptionWorkerFaultedException>(() =>
            engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None));

        Assert.Contains("paused it briefly", paused.Message, StringComparison.Ordinal);
        Assert.False(engine.UsesCpuFallback);
    }

    [Fact]
    public async Task TheStatusOfAWorkerForAnotherAccelerationIsNotShown()
    {
        var engine = Engine();
        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Then(Answer);
        await engine.TranscribeAsync(s_audio, null, false, null, CancellationToken.None);
        Assert.Equal(TranscriptionAccelerationBackend.NvidiaCuda, engine.AccelerationStatus.ActiveBackend);

        engine.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);

        Assert.Equal(_inner.AccelerationStatus, engine.AccelerationStatus);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        internal void Advance(TimeSpan by)
        {
            _now += by;
        }
    }

    // Answers each request with the next scripted step; a step that throws ends the worker.
    private sealed class ScriptedWorker(
        TranscriptionAccelerationPreference acceleration,
        Queue<Func<ScriptedWorker, TranscriptionWorkerMessage>> script
    ) : ITranscriptionWorkerConnection
    {
        public TranscriptionAccelerationPreference Acceleration { get; } = acceleration;
        public int ProcessId => 1;
        public bool IsAlive { get; private set; } = true;

        public TranscriptionWorkerState State => new(
            "small",
            "small",
            new TranscriptionAccelerationStatus(
                Acceleration == TranscriptionAccelerationPreference.NvidiaCuda
                    ? TranscriptionAccelerationBackend.NvidiaCuda
                    : TranscriptionAccelerationBackend.Cpu,
                "Scripted"
            )
        );

        public Task<TranscriptionWorkerMessage> SendAsync(
            TranscriptionWorkerMessage request,
            ReadOnlyMemory<byte> payload,
            CancellationToken ct
        )
        {
            try
            {
                return Task.FromResult(script.Dequeue()(this));
            }
            catch
            {
                IsAlive = false;
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
