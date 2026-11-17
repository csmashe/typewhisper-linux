using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SigtermShutdownTests
{
    private static readonly TimeSpan s_fallbackDelay = TimeSpan.FromSeconds(15);

    private sealed class Harness : IDisposable
    {
        public readonly ManualTimeProvider Time = new();
        public int ShutdownRequests;
        public int ForcedExits;
        public bool RegistrationDisposed;
        public Func<bool> Signal = null!;
        private readonly SigtermShutdown _sut;

        public Harness(Action? requestShutdown = null)
        {
            _sut = new SigtermShutdown(
                requestShutdown ?? (() => ShutdownRequests++),
                () => ForcedExits++,
                s_fallbackDelay,
                Time,
                handler =>
                {
                    Signal = handler;
                    return new Registration(this);
                });
        }

        public void Dispose() => _sut.Dispose();

        private sealed class Registration(Harness owner) : IDisposable
        {
            public void Dispose() => owner.RegistrationDisposed = true;
        }
    }

    [Fact]
    public void FirstSigterm_RequestsNormalQuit_AndCancelsTermination()
    {
        using var harness = new Harness();

        Assert.True(harness.Signal());

        Assert.Equal(1, harness.ShutdownRequests);
        Assert.Equal(0, harness.ForcedExits);
    }

    [Fact]
    public void SecondSigterm_TerminatesAtOnce()
    {
        using var harness = new Harness();
        harness.Signal();

        Assert.False(harness.Signal());

        Assert.Equal(1, harness.ShutdownRequests);
    }

    [Fact]
    public void QuitThatDoesNotFinish_IsForcedAfterFallbackDelay()
    {
        using var harness = new Harness();
        harness.Signal();

        harness.Time.Advance(s_fallbackDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, harness.ForcedExits);

        harness.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, harness.ForcedExits);
    }

    [Fact]
    public void FailedQuitRequest_ForcesExit()
    {
        using var harness = new Harness(
            () => throw new InvalidOperationException("dispatcher gone"));

        Assert.True(harness.Signal());

        Assert.Equal(1, harness.ForcedExits);
    }

    [Fact]
    public void NoFallback_WithoutSigterm()
    {
        using var harness = new Harness();

        harness.Time.Advance(s_fallbackDelay * 2);

        Assert.Equal(0, harness.ForcedExits);
        Assert.Equal(0, harness.ShutdownRequests);
    }

    [Fact]
    public void Dispose_ReleasesSignalRegistration()
    {
        var harness = new Harness();

        harness.Dispose();

        Assert.True(harness.RegistrationDisposed);
    }
}
