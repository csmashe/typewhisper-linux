using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LogindSessionActivityMonitorTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void DeriveInputAllowed_RequiresActiveAndUnlocked(
        bool active,
        bool lockedHint,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            LogindSessionActivityMonitor.DeriveInputAllowed(active, lockedHint)
        );
    }
}
