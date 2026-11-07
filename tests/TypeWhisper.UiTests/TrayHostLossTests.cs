using System.Runtime.Versioning;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.UiTests;

/// <summary>
///     Close-to-tray re-probes the host at close time; a host that left after startup must
///     read as unavailable, or the close hides the window with no tray to restore it from.
/// </summary>
[Collection("ui")]
[SupportedOSPlatform("linux")]
public sealed class TrayHostLossTests(UiSession ui)
{
    [Fact]
    public async Task RecheckReportsHostLossAndRecovery()
    {
        await ui.Session.Dispatch(() =>
        {
            var runner = new TrayHostRunner { HostRegistered = true };
            using var tray = new TrayIconService(runner);
            var changes = 0;
            tray.AvailabilityChanged += (_, _) => changes++;

            tray.Initialize();
            Assert.True(tray.IsTrayAvailable);

            runner.HostRegistered = false;
            Assert.False(tray.RecheckAvailability());
            Assert.False(tray.IsTrayAvailable);
            Assert.Equal(1, changes);

            runner.HostRegistered = true;
            Assert.True(tray.RecheckAvailability());
            Assert.True(tray.IsTrayAvailable);
            Assert.Equal(2, changes);
        }, CancellationToken.None);
    }

    private sealed class TrayHostRunner : HeadlessProcessRunner
    {
        public bool HostRegistered { get; set; }

        public override Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            bool detachAfterExit = false,
            CancellationToken ct = default
        )
        {
            return fileName == "gdbus"
                ? Task.FromResult(
                    new ProcessRunResult(true, false, 0, HostRegistered ? "(<true>,)\n" : "(<false>,)\n", "")
                )
                : base.RunAsync(fileName, args, environment, standardInput, timeout, detachAfterExit, ct);
        }
    }
}
