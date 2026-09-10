using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

// AT-SPI text reads run against arbitrary third-party apps, so "this element has no readable text"
// is the normal case, not a fault. Misclassifying it puts a permanent entry in the user-facing
// error log every time the user focuses an app the feature simply cannot see.
public class AtSpiReadErrorClassificationTests
{
    // at-spi 2.60 (Fedora) signals "no Text interface" this way...
    [Theory]
    [InlineData("org.freedesktop.DBus.Error.InvalidArgs: No such interface org.a11y.atspi.Text")]
    [InlineData("org.freedesktop.DBus.Error.UnknownObject: No such object path")]
    [InlineData("org.freedesktop.DBus.Error.UnknownInterface: No such interface")]
    [InlineData("org.freedesktop.DBus.Error.UnknownMethod: No such method")]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown: The name is not registered")]
    [InlineData("org.freedesktop.DBus.Error.NoReply: Message did not receive a reply")]
    [InlineData("org.freedesktop.DBus.Error.Disconnected: Connection is closed")]
    public void KnownUnreadableTargetErrors_AreBenign(string message)
    {
        Assert.True(AtSpiEventClient.IsBenignReadErrorMessage(message));
    }

    // ...while at-spi 2.52 (Ubuntu/Mint, and so Linux Mint) answers the identical property Get with
    // the generic error name plus "Get failed". Same meaning, different wire text — and left
    // unclassified it was the one that clogged the error log.
    [Fact]
    public void GenericPropertyGetFailure_IsBenign()
    {
        Assert.True(
            AtSpiEventClient.IsBenignReadErrorMessage(
                "org.freedesktop.DBus.Error.Failed: Get failed"
            )
        );
    }

    // The generic name on its own is D-Bus's catch-all: suppressing all of it would hide real
    // faults, so only the "Get failed" pairing is treated as expected.
    [Theory]
    [InlineData("org.freedesktop.DBus.Error.Failed: Out of memory")]
    [InlineData("org.freedesktop.DBus.Error.Failed")]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied: Rejected send message")]
    [InlineData("System.InvalidOperationException: something genuinely broke")]
    public void UnexpectedErrors_AreNotBenign(string message)
    {
        Assert.False(AtSpiEventClient.IsBenignReadErrorMessage(message));
    }
}
